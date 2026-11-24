using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MotorBench.Web;

public enum BenchRole
{
    Viewer,
    Operator,
    Engineer,
}

/// <summary>One entry of the "Users" configuration section.</summary>
public sealed class UserAccount
{
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public BenchRole Role { get; set; } = BenchRole.Viewer;
}

/// <summary>PBKDF2-SHA256 password hashes: <c>pbkdf2-sha256$iterations$salt$hash</c> (base64).</summary>
public static class PasswordHash
{
    private const int Iterations = 600_000; // OWASP 2023 recommendation for PBKDF2-SHA256
    private const int SaltSize = 16;
    private const int HashSize = 32;

    // compared against when the user name is unknown, so timing does not reveal which names exist
    private static readonly string Dummy = Create("motorbench-dummy-password");

    public static string Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (!TryParse(stored ?? Dummy, out int iterations, out var salt, out var expected))
            return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected) && stored is not null;
    }

    public static bool IsValid(string stored) => TryParse(stored, out _, out _, out _);

    private static bool TryParse(string stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = hash = [];
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out iterations) || iterations < 100_000)
            return false;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length >= 16 && hash.Length >= 16;
    }
}

public static class Auth
{
    public const string OperatorPolicy = "operator";
    public const string EngineerPolicy = "engineer";
    private const string LoginRateLimit = "login";

    /// <summary>Loads and checks the "Users" section. Duplicate names or bad hashes stop the app at startup.</summary>
    public static UserAccount[] LoadUsers(IConfiguration config)
    {
        var users = config.GetSection("Users").Get<UserAccount[]>() ?? [];
        foreach (var u in users)
        {
            if (string.IsNullOrWhiteSpace(u.Name))
                throw new InvalidOperationException("Users: every user needs a Name");
            if (!PasswordHash.IsValid(u.PasswordHash))
                throw new InvalidOperationException($"Users: '{u.Name}' has no valid PasswordHash. Create one with: MotorBench.Web hash-password");
        }
        var duplicate = users.GroupBy(u => u.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Users: '{duplicate.Key}' is listed twice");
        return users;
    }

    /// <summary>
    /// Refuses to start if the HMI is reachable from the network without HTTPS
    /// and logins. Without users it only ever listens on this machine.
    /// </summary>
    public static void CheckExposure(string urls, UserAccount[] users)
    {
        var list = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (list.All(IsLoopbackUrl))
            return;
        if (users.Length == 0 || list.Any(u => !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"urls '{urls}' make the HMI reachable from the network. That needs HTTPS URLs only and at least one " +
                "user in the Users section (this page can start a motor). Also add the host name to AllowedHosts.");
    }

    internal static bool IsLoopbackUrl(string url)
    {
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
            return false;
        var rest = url[(scheme + 3)..];
        var host = rest.StartsWith('[') ? rest[..(rest.IndexOf(']') + 1)] : rest.Split(':', '/')[0];
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
    }

    public static void AddBenchAuth(this IServiceCollection services, UserAccount[] users)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "MotorBench.Auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(12); // one shift
                o.SlidingExpiration = true;
                // an API: answer 401/403 instead of redirecting to a login page
                o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
                // a removed user or changed role invalidates existing cookies
                o.Events.OnValidatePrincipal = c =>
                {
                    var user = users.FirstOrDefault(u => u.Name == c.Principal?.Identity?.Name);
                    if (user is null || c.Principal!.FindFirstValue(ClaimTypes.Role) != user.Role.ToString())
                        c.RejectPrincipal();
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(OperatorPolicy, p => p.RequireRole(nameof(BenchRole.Operator), nameof(BenchRole.Engineer)))
            .AddPolicy(EngineerPolicy, p => p.RequireRole(nameof(BenchRole.Engineer)));

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.AddPolicy(LoginRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
        });
    }

    /// <summary>
    /// Local mode (no users configured, localhost only): everyone using this
    /// machine is the "local" engineer, so the one-command demo needs no login.
    /// </summary>
    public static IApplicationBuilder UseLocalUser(this IApplicationBuilder app)
    {
        var local = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "local"), new Claim(ClaimTypes.Role, nameof(BenchRole.Engineer))], "Local"));
        return app.Use((ctx, next) =>
        {
            ctx.User = local;
            return next(ctx);
        });
    }

    public static void MapAuthApi(this WebApplication app, UserAccount[] users)
    {
        bool loginRequired = users.Length > 0;

        app.MapGet("/api/me", (HttpContext ctx) => new
        {
            loginRequired,
            user = ctx.User.Identity?.IsAuthenticated == true
                ? new { name = ctx.User.Identity.Name, role = ctx.User.FindFirstValue(ClaimTypes.Role) }
                : null,
        }).AllowAnonymous();

        if (!loginRequired)
            return;

        app.MapPost("/api/login", async (LoginRequest req, HttpContext ctx, AuditLog audit) =>
        {
            var user = users.FirstOrDefault(u => u.Name == req.Name);
            bool ok = PasswordHash.Verify(req.Password ?? "", user?.PasswordHash);
            audit.Write(req.Name ?? "", "/api/login", ok ? "ok" : "wrong user name or password", ok ? 200 : 401);
            if (!ok)
                return Results.Problem("Wrong user name or password", statusCode: 401);

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, user!.Name), new Claim(ClaimTypes.Role, user.Role.ToString())],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(new ClaimsPrincipal(identity));
            return Results.Ok();
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimit);

        app.MapPost("/api/logout", async (HttpContext ctx, ControlLock control) =>
        {
            control.Release(ctx.User.Identity?.Name ?? "", Api.ClientId(ctx));
            await ctx.SignOutAsync();
            return Results.Ok();
        });
    }

    private sealed record LoginRequest(string? Name, string? Password);
}
