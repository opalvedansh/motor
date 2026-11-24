using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using MotorBench.Web;
using static MotorBench.Tests.WebDashboardTests;

namespace MotorBench.Tests;

// Logins, roles, control lock, audit log and startup checks of the web HMI.
public sealed class WebSecurityTests : IDisposable
{
    private const string Password = "correct-horse-battery";
    private static readonly string Hash = PasswordHash.Create(Password);

    private readonly string _dataDir = Directory.CreateTempSubdirectory("motorbench-").FullName;

    public void Dispose() => Directory.Delete(_dataDir, recursive: true);

    private WebApplicationFactory<Program> CreateApp(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DataDir", _dataDir);
            foreach (var (key, value) in settings)
                b.UseSetting(key, value);
        });

    private WebApplicationFactory<Program> CreateAppWithUsers() => CreateApp(
        ("Users:0:Name", "viewer"), ("Users:0:PasswordHash", Hash), ("Users:0:Role", "Viewer"),
        ("Users:1:Name", "op"), ("Users:1:PasswordHash", Hash), ("Users:1:Role", "Operator"),
        ("Users:2:Name", "eng"), ("Users:2:PasswordHash", Hash), ("Users:2:Role", "Engineer"));

    private static Task<HttpResponseMessage> Login(HttpClient http, string name, string password = Password) =>
        http.PostAsJsonAsync("/api/login", new { name, password });

    private static async Task<HttpStatusCode> Post(HttpClient http, string path, object? body = null) =>
        (await (body is null ? http.PostAsync(path, null) : http.PostAsJsonAsync(path, body))).StatusCode;

    [Fact]
    public void Password_hashes_are_salted_and_verified()
    {
        var a = PasswordHash.Create("secret-password");
        var b = PasswordHash.Create("secret-password");

        Assert.NotEqual(a, b);
        Assert.True(PasswordHash.Verify("secret-password", a));
        Assert.False(PasswordHash.Verify("Secret-password", a));
        Assert.False(PasswordHash.Verify("secret-password", null));
        Assert.False(PasswordHash.IsValid("plain-text-password"));
        Assert.False(PasswordHash.IsValid("pbkdf2-sha256$1000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAA==")); // too few iterations
    }

    [Fact]
    public void Network_exposure_needs_https_and_users()
    {
        UserAccount[] none = [];
        UserAccount[] users = [new() { Name = "op", PasswordHash = Hash, Role = BenchRole.Operator }];

        Auth.CheckExposure("http://localhost:5080", none);
        Auth.CheckExposure("http://127.0.0.1:5080;http://[::1]:5080", none);
        Auth.CheckExposure("https://*:5443", users);

        Assert.Throws<InvalidOperationException>(() => Auth.CheckExposure("http://0.0.0.0:5080", none));
        Assert.Throws<InvalidOperationException>(() => Auth.CheckExposure("https://*:5443", none));
        Assert.Throws<InvalidOperationException>(() => Auth.CheckExposure("http://+:80", users));
        Assert.Throws<InvalidOperationException>(() => Auth.CheckExposure("http://localhost:5080;http://192.168.0.5:5080", users));
    }

    [Fact]
    public void Bad_configuration_stops_the_app_at_startup()
    {
        var exposed = Assert.ThrowsAny<Exception>(() => CreateApp(("urls", "http://0.0.0.0:5080")).CreateClient());
        Assert.Contains("HTTPS", exposed.ToString());

        var port = Assert.ThrowsAny<Exception>(() => CreateApp(("Plc:Port", "70000")).CreateClient());
        Assert.Contains("Port", port.ToString());

        var hash = Assert.ThrowsAny<Exception>(() => CreateApp(("Users:0:Name", "op"), ("Users:0:PasswordHash", "secret")).CreateClient());
        Assert.Contains("hash-password", hash.ToString());
    }

    [Fact]
    public async Task Logins_and_roles_decide_who_can_do_what()
    {
        await using var app = CreateAppWithUsers();
        using var http = CreateClient(app);

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/status")).StatusCode);
        var me = await http.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.True(me.GetProperty("loginRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(http, "op", "wrong-password")).StatusCode);

        // viewer: may watch, may not operate
        Assert.Equal(HttpStatusCode.OK, (await Login(http, "viewer")).StatusCode);
        await WaitFor(http, Online, 10);
        Assert.Equal(HttpStatusCode.Forbidden, await Post(http, "/api/control/take"));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(http, "/api/stop"));
        Assert.Equal(HttpStatusCode.OK, await Post(http, "/api/logout"));

        // operator: runs the bench, but no fault injection
        Assert.Equal(HttpStatusCode.OK, (await Login(http, "op")).StatusCode);
        await TakeControl(http);
        Assert.Equal(HttpStatusCode.OK, await Post(http, "/api/setpoints", new { speedRpm = 600, loadPct = 0 }));
        Assert.Equal(HttpStatusCode.Forbidden, await Post(http, "/api/sim/estop", new { on = true }));

        // engineer in another browser: can't take over, but can always stop
        using var eng = CreateClient(app);
        Assert.Equal(HttpStatusCode.OK, (await Login(eng, "eng")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, await Post(eng, "/api/control/take"));
        Assert.Equal(HttpStatusCode.OK, await Post(eng, "/api/stop"));

        var audit = await File.ReadAllTextAsync(Path.Combine(_dataDir, "audit.csv"));
        Assert.Contains("op,/api/login,wrong user name or password,401", audit);
        Assert.Contains("viewer,/api/control/take,,403", audit);
        Assert.Contains("eng,/api/stop,,200", audit);
        Assert.DoesNotContain(Password, audit);
    }

    [Fact]
    public async Task One_tab_has_control_and_any_tab_can_stop()
    {
        await using var app = CreateApp(); // local mode: no users, everyone is "local"
        using var a = CreateClient(app);
        using var b = CreateClient(app);
        await WaitFor(a, Online, 10);

        await TakeControl(a);
        Assert.Equal(HttpStatusCode.Conflict, await Post(b, "/api/control/take"));
        Assert.Equal(HttpStatusCode.Conflict, await Post(b, "/api/setpoints", new { speedRpm = 600, loadPct = 0 }));
        Assert.Equal(HttpStatusCode.OK, await Post(b, "/api/stop"));
        Assert.Equal(HttpStatusCode.OK, await Post(a, "/api/setpoints", new { speedRpm = 600, loadPct = 0 }));

        var status = await a.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.True(status.GetProperty("control").GetProperty("youHaveControl").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, await Post(a, "/api/control/release"));
        await TakeControl(b);

        var audit = await File.ReadAllTextAsync(Path.Combine(_dataDir, "audit.csv"));
        Assert.Contains("local,/api/setpoints,\"{\"\"speedRpm\"\":600,\"\"loadPct\"\":0}\",409", audit);
        Assert.Contains("local,/api/setpoints,\"{\"\"speedRpm\"\":600,\"\"loadPct\"\":0}\",200", audit);
    }

    [Fact]
    public void Control_lock_expires_when_the_holder_stops_polling()
    {
        var time = new ManualTime();
        var control = new ControlLock(time);

        Assert.True(control.TryTake("alice", "tab-1"));
        Assert.False(control.TryTake("bob", "tab-2"));

        time.Advance(TimeSpan.FromSeconds(4));
        control.Seen("alice", "tab-1"); // still polling
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(control.TryTake("bob", "tab-2"));
        Assert.Equal("alice", control.Holder);

        time.Advance(ControlLock.Expiry + TimeSpan.FromSeconds(1)); // alice closed her page
        Assert.Null(control.Holder);
        Assert.True(control.TryTake("bob", "tab-2"));
        Assert.False(control.Holds("alice", "tab-1"));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\", ok", "\"say \"\"hi\"\", ok\"")]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("two\nlines", "two lines")]
    public void Audit_fields_are_safe_to_open_in_excel(string value, string expected) =>
        Assert.Equal(expected, AuditLog.Field(value));

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
