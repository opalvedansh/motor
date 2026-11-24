using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using MotorBench.Web;
using static MotorBench.Tests.WebDashboardTests;

namespace MotorBench.Tests;

// Every run attempt leaves a record; refused ones leave none; old run files expire.
public sealed class RunTraceabilityTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("motorbench-").FullName;

    public void Dispose() => Directory.Delete(_dataDir, recursive: true);

    private WebApplicationFactory<Program> CreateApp() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("DataDir", _dataDir));

    private static Task<HttpResponseMessage> StartRun(HttpClient http, string? dutSerial) =>
        http.PostAsJsonAsync("/api/sequence/start", new { profile = "standard_sweep", dutSerial });

    private static Task<HttpResponseMessage> EStop(HttpClient http, bool on) =>
        http.PostAsJsonAsync("/api/sim/estop", new { on });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("has space")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDE")] // 41 characters
    public async Task Run_needs_a_valid_dut_serial(string? serial)
    {
        await using var app = CreateApp();
        using var http = CreateClient(app);
        await WaitFor(http, Online, 10);
        await TakeControl(http);

        Assert.Equal(HttpStatusCode.BadRequest, (await StartRun(http, serial)).StatusCode);
        Assert.Empty(Directory.GetFiles(_dataDir, "raw_*"));
    }

    [Fact]
    public async Task Refused_runs_leave_no_files_and_aborted_runs_are_recorded()
    {
        await using var app = CreateApp();
        using var http = CreateClient(app);
        await WaitFor(http, Online, 10);
        await TakeControl(http);

        // alarm active: the runner refuses before anything runs
        await EStop(http, true);
        await WaitFor(http, d => d.GetProperty("status").GetProperty("state").GetString() == "Fault", 5);
        Assert.Equal(HttpStatusCode.Conflict, (await StartRun(http, "SN-1")).StatusCode);
        Assert.Empty(Directory.GetFiles(_dataDir, "raw_*"));
        Assert.Empty(Directory.GetFiles(_dataDir, "result_*"));

        await EStop(http, false);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/api/reset", null)).StatusCode);

        // E-stop during the first step: aborted with no finished steps, still on record
        Assert.Equal(HttpStatusCode.OK, (await StartRun(http, "SN-2")).StatusCode);
        await WaitFor(http, d => d.GetProperty("status").GetProperty("speedRpm").GetInt32() > 100, 5);
        await EStop(http, true);
        await WaitFor(http, d => d.GetProperty("sequence").GetProperty("state").GetString() == "Aborted", 5);

        var run = (await http.GetFromJsonAsync<JsonElement>("/api/runs")).EnumerateArray().Single();
        Assert.Equal("SN-2", run.GetProperty("dutSerial").GetString());
        Assert.StartsWith("aborted: bench alarm", run.GetProperty("outcome").GetString());
        Assert.Equal(0, run.GetProperty("steps").GetInt32());
        Assert.True(File.Exists(Path.Combine(_dataDir, run.GetProperty("rawData").GetString()!)));
    }

    [Fact]
    public void Retention_deletes_old_run_files_only()
    {
        var now = new DateTime(2026, 11, 23, 12, 0, 0);
        string Make(string name, int daysOld)
        {
            var path = Path.Combine(_dataDir, name);
            File.WriteAllText(path, "x");
            File.SetLastWriteTime(path, now.AddDays(-daysOld));
            return path;
        }
        var oldResult = Make("result_sweep_20250101_000000.csv", 400);
        var oldRaw = Make("raw_sweep_SN-1_20250101_000000.csv", 400);
        var newResult = Make("result_sweep_20261120_000000.csv", 3);
        var audit = Make("audit.csv", 400);
        var alarms = Make("alarm_history.csv", 400);

        Assert.Equal(0, RunFiles.Prune(_dataDir, 0, now)); // 0 = keep forever
        Assert.Equal(2, RunFiles.Prune(_dataDir, 365, now));

        Assert.False(File.Exists(oldResult));
        Assert.False(File.Exists(oldRaw));
        Assert.True(File.Exists(newResult));
        Assert.True(File.Exists(audit));
        Assert.True(File.Exists(alarms));
    }

    [Fact]
    public void Run_list_reads_reports_from_the_winforms_hmi_too()
    {
        // written by StepReport without the web HMI's extra header lines
        File.WriteAllLines(Path.Combine(_dataDir, "result_quick_check_20261101_090000.csv"),
        [
            "# profile,quick_check",
            "# finished,2026-11-01 09:00:00",
            "# outcome,aborted: bench alarm: E-stop, operator",
            "step,speed_sp_rpm,load_pct,dwell_s,settle_s,avg_speed_rpm,avg_torque_nm,max_temp_c",
            "1,500,0,3,1.82,500.0,1.00,25.4",
        ]);

        var run = Assert.Single(RunFiles.List(_dataDir, 10));
        Assert.Equal("quick_check", run.Profile);
        Assert.Null(run.DutSerial);
        Assert.Equal("aborted: bench alarm: E-stop, operator", run.Outcome);
        Assert.Equal(new DateTime(2026, 11, 1, 9, 0, 0), run.Finished);
        Assert.Equal(1, run.Steps);
    }

    [Theory]
    [InlineData("result_x.csv", true)]
    [InlineData("raw_x_SN-1_20260101_000000.csv", true)]
    [InlineData("audit.csv", false)]
    [InlineData("alarm_history.csv", false)]
    [InlineData("../result_x.csv", false)]
    [InlineData("result_x.txt", false)]
    public void Only_run_files_can_be_downloaded(string name, bool allowed) =>
        Assert.Equal(allowed, RunFiles.IsRunFile(name));
}
