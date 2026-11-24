using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MotorBench.Plc;
using MotorBench.Sequencing;

namespace MotorBench.Web;

public static partial class Api
{
    // letters, digits, . _ - only: the serial goes into file names
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,39}$")]
    private static partial Regex DutSerialPattern();

    /// <summary>
    /// Every request from the page carries a per-tab id in this header. It
    /// identifies who holds control, and it blocks cross-site requests: another
    /// origin cannot add a custom header without a CORS preflight, which this
    /// server never grants.
    /// </summary>
    public const string ClientHeader = "X-MotorBench-Client";

    public static string? ClientId(HttpContext ctx)
    {
        string? id = ctx.Request.Headers[ClientHeader];
        return id is { Length: > 0 and <= 64 } ? id : null;
    }

    /// <summary>Set at release build time (-p:Version=1.2.3); local builds are 0.0.0-dev.</summary>
    public static readonly string Version =
        typeof(Api).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    private static string UserName(HttpContext ctx) => ctx.User.Identity?.Name ?? "anonymous";

    public static IApplicationBuilder RequireClientHeader(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && ClientId(ctx) is null)
        {
            await Results.Problem($"Missing {ClientHeader} header", statusCode: 403).ExecuteAsync(ctx);
            return;
        }
        await next();
    });

    /// <summary>Writes one audit row per API POST, including refused ones. Login audits itself (no passwords here).</summary>
    public static IApplicationBuilder UseAudit(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path;
        if (!HttpMethods.IsPost(ctx.Request.Method) || !path.StartsWithSegments("/api") || path.StartsWithSegments("/api/login"))
        {
            await next();
            return;
        }

        ctx.Request.EnableBuffering();
        var buffer = new byte[512];
        int read = await ctx.Request.Body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
        ctx.Request.Body.Position = 0;
        var body = Encoding.UTF8.GetString(buffer, 0, read);

        try
        {
            await next();
        }
        finally
        {
            ctx.RequestServices.GetRequiredService<AuditLog>().Write(UserName(ctx), path, body, ctx.Response.StatusCode);
        }
    });

    /// <summary>Only the tab holding the control lock may run this.</summary>
    private static RouteHandlerBuilder NeedsControl(this RouteHandlerBuilder endpoint) => endpoint.AddEndpointFilter(async (fc, next) =>
    {
        var ctx = fc.HttpContext;
        var control = ctx.RequestServices.GetRequiredService<ControlLock>();
        if (control.Holds(UserName(ctx), ClientId(ctx)))
            return await next(fc);
        return Results.Problem(
            control.Holder is { } holder ? $"{holder} has control of the bench." : "Take control of the bench first.",
            statusCode: 409);
    });

    public static void MapBenchApi(this WebApplication app)
    {
        var bench = app.Services.GetRequiredService<BenchService>();
        var settings = app.Services.GetRequiredService<WebSettings>();
        var control = app.Services.GetRequiredService<ControlLock>();
        const string op = Auth.OperatorPolicy, eng = Auth.EngineerPolicy;

        // For service monitors: no login, and no details beyond up/ready (the reason is in the log).
        app.MapGet("/healthz", () => "ok").AllowAnonymous();
        app.MapGet("/readyz", () => bench.Snapshot.Error is null ? Results.Text("ready") : Results.Text("not ready", statusCode: 503))
            .AllowAnonymous();
        app.MapGet("/api/version", () => new { Version, RegisterMap = RegisterMap.CurrentMapVersion });

        app.MapGet("/api/status", (HttpContext ctx) =>
        {
            string user = UserName(ctx);
            string? client = ClientId(ctx);
            control.Seen(user, client); // polling keeps the holder's lock alive
            var s = bench.Snapshot;
            return new
            {
                s.Plc,
                s.Error,
                s.Status,
                s.Sequence,
                s.Alarms,
                s.UpdatedUtc,
                user = new { name = user, role = ctx.User.FindFirstValue(ClaimTypes.Role) },
                control = new { holder = control.Holder, youHaveControl = control.Holds(user, client) },
            };
        });

        app.MapPost("/api/control/take", (HttpContext ctx) => control.TryTake(UserName(ctx), ClientId(ctx)!)
            ? Results.Ok()
            : Results.Problem($"{control.Holder} has control of the bench.", statusCode: 409)).RequireAuthorization(op);
        app.MapPost("/api/control/release", (HttpContext ctx) =>
        {
            control.Release(UserName(ctx), ClientId(ctx));
            return Results.Ok();
        });

        app.MapPost("/api/start", () => Run(bench.StartAsync)).RequireAuthorization(op).NeedsControl();
        // anyone allowed to operate may stop, with or without the control lock
        app.MapPost("/api/stop", () => Run(bench.StopBenchAsync)).RequireAuthorization(op);
        app.MapPost("/api/reset", () => Run(bench.ResetAsync)).RequireAuthorization(op).NeedsControl();
        app.MapPost("/api/setpoints", (Setpoints sp) => Run(() => bench.SetSetpointsAsync(sp.SpeedRpm, sp.LoadPct)))
            .RequireAuthorization(op).NeedsControl();
        app.MapPost("/api/sim/estop", (Toggle t) => Run(() => bench.SetSimEStopAsync(t.On))).RequireAuthorization(eng).NeedsControl();
        app.MapPost("/api/sim/runaway", (Toggle t) => Run(() => bench.SetSimRunawayAsync(t.On))).RequireAuthorization(eng).NeedsControl();

        app.MapGet("/api/profiles", () => ProfileFiles(settings).Select(ProfileInfo));

        app.MapPost("/api/sequence/start", async (SequenceStart req, HttpContext ctx) =>
        {
            if (req.DutSerial is null || !DutSerialPattern().IsMatch(req.DutSerial))
                return Results.Problem("DUT serial is required: up to 40 letters, digits, '.', '_' or '-'", statusCode: 400);

            // only names from the profile folder, never a path from the request
            var file = ProfileFiles(settings).FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == req.Profile);
            if (file is null)
                return Results.Problem($"Unknown profile '{req.Profile}'", statusCode: 404);

            // hash and parse the same bytes, so the recorded hash is exactly what ran
            var bytes = await File.ReadAllBytesAsync(file);
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            TestProfile profile;
            try
            {
                using var reader = new StreamReader(new MemoryStream(bytes)); // drops a BOM, as File.ReadLines does
                profile = TestProfile.Parse(req.Profile!, reader.ReadToEnd().Split('\n')); // Parse trims the '\r'
            }
            catch (ProfileFormatException ex)
            {
                return Results.Problem($"{req.Profile}.csv {ex.Message}", statusCode: 400);
            }
            return await Run(() => bench.StartSequenceAsync(profile, req.DutSerial, UserName(ctx), sha256));
        }).RequireAuthorization(op).NeedsControl();
        app.MapPost("/api/sequence/hold", () => Run(bench.HoldAsync)).RequireAuthorization(op).NeedsControl();
        app.MapPost("/api/sequence/resume", () => Run(bench.ResumeAsync)).RequireAuthorization(op).NeedsControl();
        app.MapPost("/api/sequence/abort", () => Run(bench.AbortSequenceAsync)).RequireAuthorization(op);

        app.MapGet("/api/runs", () => RunFiles.List(settings.DataDir, max: 100));

        // run files only: no paths, and not the audit or alarm logs
        app.MapGet("/api/reports/{name}", (string name) =>
        {
            var path = Path.Combine(settings.DataDir, name);
            return RunFiles.IsRunFile(name) && File.Exists(path)
                ? Results.File(path, "text/csv", name)
                : Results.NotFound();
        });
    }

    private static async Task<IResult> Run(Func<Task> command)
    {
        try
        {
            await command();
            return Results.Ok();
        }
        catch (BenchCommandException ex)
        {
            return Results.Problem(ex.Message, statusCode: ex.StatusCode);
        }
    }

    private static string[] ProfileFiles(WebSettings settings) =>
        Directory.Exists(settings.ProfileDir) ? [.. Directory.GetFiles(settings.ProfileDir, "*.csv").Order()] : [];

    private static object ProfileInfo(string file)
    {
        try
        {
            var p = TestProfile.Load(file);
            return new { p.Name, Steps = p.Steps.Count, TotalSeconds = p.TotalDwellSeconds, Error = (string?)null };
        }
        catch (ProfileFormatException ex)
        {
            // show the broken file in the list instead of failing the whole request
            return new { Name = Path.GetFileNameWithoutExtension(file), Steps = 0, TotalSeconds = 0.0, Error = (string?)ex.Message };
        }
    }

    private sealed record Setpoints(int SpeedRpm, int LoadPct);
    private sealed record Toggle(bool On);
    private sealed record SequenceStart(string? Profile, string? DutSerial);
}
