using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MotorBench.Modbus;
using MotorBench.Plc;
using MotorBench.Sim;
using MotorBench.Web;

namespace MotorBench.Tests;

// Browser API -> BenchService -> Modbus TCP -> simulator, the whole web HMI stack.
public sealed class WebDashboardTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("motorbench-").FullName;

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_dataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(_dataDir, recursive: true);
    }

    private WebApplicationFactory<Program> CreateApp(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DataDir", _dataDir);
            foreach (var (key, value) in settings)
                b.UseSetting(key, value);
        });

    internal static HttpClient CreateClient(WebApplicationFactory<Program> app)
    {
        var http = app.CreateClient();
        http.DefaultRequestHeaders.Add(Api.ClientHeader, Guid.NewGuid().ToString());
        return http;
    }

    internal static async Task TakeControl(HttpClient http) =>
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/control/take", null)).StatusCode);

    [Fact]
    public async Task Quick_check_profile_runs_to_completion_and_writes_a_report()
    {
        await using var app = CreateApp();
        using var http = CreateClient(app);
        await WaitFor(http, Online, 10);
        await TakeControl(http);

        var start = await http.PostAsJsonAsync("/api/sequence/start", new { profile = "quick_check", dutSerial = "M-0042" });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        // manual control is locked out while the sequence owns the bench
        var manual = await http.PostAsJsonAsync("/api/setpoints", new { speedRpm = 100, loadPct = 0 });
        Assert.Equal(HttpStatusCode.Conflict, manual.StatusCode);

        var done = await WaitFor(http, d => d.GetProperty("sequence").GetProperty("state").GetString() is "Completed" or "Aborted", 40);
        var seq = done.GetProperty("sequence");
        Assert.Equal("Completed", seq.GetProperty("state").GetString());

        var results = seq.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal([500, 1500, 3000], results.Select(r => r.GetProperty("step").GetProperty("speedRpm").GetInt32()));
        foreach (var r in results)
            Assert.Equal(r.GetProperty("step").GetProperty("speedRpm").GetDouble(), r.GetProperty("avgSpeedRpm").GetDouble(), tolerance: 20);

        var report = seq.GetProperty("report").GetString()!;
        Assert.True(File.Exists(Path.Combine(_dataDir, report)));
        var csv = await http.GetStringAsync($"/api/reports/{report}");
        Assert.Contains("# outcome,completed", csv);

        // traceability: who tested which unit with exactly which profile, plus the raw data
        var profileHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "profiles", "quick_check.csv"))));
        Assert.Contains("# dut_serial,M-0042", csv);
        Assert.Contains("# operator,local", csv);
        Assert.Contains($"# profile_sha256,{profileHash}", csv);
        var rawName = csv.Split('\n').Single(l => l.StartsWith("# raw_data,"))["# raw_data,".Length..].Trim();
        var raw = await http.GetStringAsync($"/api/reports/{rawName}");
        Assert.True(raw.Split('\n').Count(l => l.Contains(",Running,")) > 50, "raw data has a row per poll while running");

        var runs = await http.GetFromJsonAsync<JsonElement>("/api/runs");
        var run = runs.EnumerateArray().Single();
        Assert.Equal("M-0042", run.GetProperty("dutSerial").GetString());
        Assert.Equal("local", run.GetProperty("operator").GetString());
        Assert.Equal("completed", run.GetProperty("outcome").GetString());
        Assert.Equal(3, run.GetProperty("steps").GetInt32());
        Assert.Equal(rawName, run.GetProperty("rawData").GetString());

        // the download endpoint serves run files only
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/reports/audit.csv")).StatusCode);

        var escape = await http.GetAsync("/api/reports/..%2F..%2Fappsettings.json");
        Assert.Equal(HttpStatusCode.NotFound, escape.StatusCode);
    }

    [Fact]
    public async Task Cross_site_and_wrong_host_requests_are_rejected()
    {
        await using var app = CreateApp();
        using var http = app.CreateClient(); // no X-MotorBench header, like a form post from another site
        await WaitFor(http, Online, 10);

        var noHeader = await http.PostAsync("/api/start", null);
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);

        // DNS rebinding: the browser sends the attacker's host name
        var rebound = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        rebound.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(rebound)).StatusCode);

        var status = await http.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.Equal("Idle", status.GetProperty("status").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Frozen_plc_heartbeat_takes_the_hmi_offline_until_the_scan_resumes()
    {
        await using var app = CreateApp();
        using var http = CreateClient(app);
        await WaitFor(http, Online, 10);

        await TakeControl(http);
        var sim = app.Services.GetRequiredService<SimulatorHost>();
        sim.Paused = true; // Modbus still answers, the program does not scan

        var offline = await WaitFor(http, d => d.GetProperty("error").GetString()?.Contains("heartbeat") == true, 5);
        Assert.Equal(JsonValueKind.Null, offline.GetProperty("status").ValueKind);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.PostAsync("/api/start", null)).StatusCode);

        sim.Paused = false;
        await WaitFor(http, Online, 5);
    }

    [Fact]
    public async Task Unwritable_data_folder_does_not_stop_the_poll_loop()
    {
        if (OperatingSystem.IsWindows())
            return; // uses Unix permissions
        File.SetUnixFileMode(_dataDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        await using var app = CreateApp();
        using var http = CreateClient(app);
        await WaitFor(http, Online, 10);
        await TakeControl(http);

        // raising an alarm writes alarm_history.csv, which now fails
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/api/sim/estop", new { on = true })).StatusCode);
        await WaitFor(http, d => d.GetProperty("alarms").GetArrayLength() == 1, 5);

        await Task.Delay(500);
        var status = await WaitFor(http, Online, 1);
        Assert.Equal("EStop", status.GetProperty("status").GetProperty("alarm").GetString());
    }

    [Fact]
    public async Task Shutdown_while_running_stops_the_bench_without_a_comm_loss_fault()
    {
        // external simulator, so it outlives the web app
        var bank = new RegisterBank();
        var model = new BenchModel();
        await using var server = new ModbusTcpServer(bank, IPAddress.Loopback, 0);
        server.Start();
        using var cts = new CancellationTokenSource();
        var scan = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                bank.Access((hr, coils) => model.Scan(hr, coils, 0.02));
                await Task.Delay(20);
            }
        });

        try
        {
            var app = CreateApp(("Plc:Host", "127.0.0.1"), ("Plc:Port", server.Port.ToString()));
            using (var http = CreateClient(app))
            {
                await WaitFor(http, Online, 10);
                await TakeControl(http);
                await http.PostAsJsonAsync("/api/setpoints", new { speedRpm = 1200, loadPct = 0 });
                await http.PostAsync("/api/start", null);
                await WaitFor(http, d => d.GetProperty("status").GetProperty("speedRpm").GetInt32() >= 1200, 10);
            }

            // coasting down from 1200 rpm takes ~3 s, longer than the 2 s watchdog
            await app.DisposeAsync();

            Assert.Equal((ushort)BenchState.Idle, bank.GetHolding(RegisterMap.State));
            await Task.Delay(2500); // past the watchdog timeout
            Assert.Equal((ushort)AlarmCode.None, bank.GetHolding(RegisterMap.Alarm));
        }
        finally
        {
            cts.Cancel();
            await scan;
        }
    }

    internal static bool Online(JsonElement d) =>
        d.GetProperty("error").ValueKind == JsonValueKind.Null && d.GetProperty("status").ValueKind == JsonValueKind.Object;

    internal static async Task<JsonElement> WaitFor(HttpClient http, Func<JsonElement, bool> done, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var d = await http.GetFromJsonAsync<JsonElement>("/api/status");
            if (done(d))
                return d;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"condition not met within {seconds} s, last status: {d}");
            await Task.Delay(100);
        }
    }
}
