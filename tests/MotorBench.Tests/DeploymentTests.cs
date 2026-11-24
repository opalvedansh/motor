using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using MotorBench.Modbus;
using MotorBench.Plc;
using MotorBench.Sim;
using MotorBench.Web;
using static MotorBench.Tests.WebDashboardTests;

namespace MotorBench.Tests;

// What a service manager or monitor sees, and the PLC program version check.
public sealed class DeploymentTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("motorbench-").FullName;

    public void Dispose() => Directory.Delete(_dataDir, recursive: true);

    private WebApplicationFactory<Program> CreateApp(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DataDir", _dataDir);
            foreach (var (key, value) in settings)
                b.UseSetting(key, value);
        });

    [Fact]
    public async Task Health_endpoints_need_no_login_and_report_readiness()
    {
        await using var app = CreateApp(
            ("Users:0:Name", "op"), ("Users:0:PasswordHash", PasswordHash.Create("password1")), ("Users:0:Role", "Operator"));
        using var anonymous = CreateClient(app);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/status")).StatusCode);
        Assert.Equal("ok", await anonymous.GetStringAsync("/healthz"));

        // ready once the built-in simulator answers with a running heartbeat
        var deadline = DateTime.UtcNow.AddSeconds(10);
        HttpResponseMessage ready;
        while ((ready = await anonymous.GetAsync("/readyz")).StatusCode != HttpStatusCode.OK && DateTime.UtcNow < deadline)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            await Task.Delay(100);
        }
        Assert.Equal("ready", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Version_names_the_build_and_the_register_map()
    {
        await using var app = CreateApp();
        using var http = CreateClient(app);

        var v = await http.GetFromJsonAsync<JsonElement>("/api/version");
        Assert.StartsWith("0.0.0-dev", v.GetProperty("version").GetString());
        Assert.Equal(RegisterMap.CurrentMapVersion, v.GetProperty("registerMap").GetInt32());
    }

    [Fact]
    public async Task Wrong_plc_program_is_never_written_to_and_is_picked_up_once_fixed()
    {
        // an old PLC program: answers Modbus, but has no map version (HR 8 = 0)
        var bank = new RegisterBank();
        await using var server = new ModbusTcpServer(bank, IPAddress.Loopback, 0);
        server.Start();
        using var cts = new CancellationTokenSource();
        Task? scan = null;

        try
        {
            await using var app = CreateApp(("Plc:Host", "127.0.0.1"), ("Plc:Port", server.Port.ToString()));
            using var http = CreateClient(app);

            var d = await WaitFor(http, d => d.GetProperty("error").GetString()?.Contains("register map version 0") == true, 10);
            Assert.Contains("needs 1", d.GetProperty("error").GetString());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/readyz")).StatusCode);
            await TakeControl(http);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.PostAsync("/api/start", null)).StatusCode);
            Assert.Equal(0, bank.GetHolding(RegisterMap.HmiWatchdog)); // not even the watchdog was written

            // load the right program: the HMI reconnects by itself
            var model = new BenchModel();
            scan = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    bank.Access((hr, coils) => model.Scan(hr, coils, 0.02));
                    await Task.Delay(20);
                }
            });
            await WaitFor(http, Online, 10);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/readyz")).StatusCode);
        }
        finally
        {
            cts.Cancel();
            if (scan is not null)
                await scan;
        }
    }
}
