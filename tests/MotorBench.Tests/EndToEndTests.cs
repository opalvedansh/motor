using System.Net;
using MotorBench.Logging;
using MotorBench.Modbus;
using MotorBench.Plc;
using MotorBench.Sim;

namespace MotorBench.Tests;

// BenchPlc -> Modbus TCP -> simulator, the same path the HMI uses.
public class EndToEndTests
{
    [Fact]
    public async Task Start_command_is_taken_and_the_bench_runs()
    {
        var bank = new RegisterBank();
        var model = new BenchModel();
        await using var server = new ModbusTcpServer(bank, IPAddress.Loopback, 0);
        server.Start();

        using var cts = new CancellationTokenSource();
        var scanLoop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                bank.Access((hr, coils) => model.Scan(hr, coils, 0.02));
                await Task.Delay(10);
            }
        });

        try
        {
            using var modbus = new ModbusTcpClient();
            await modbus.ConnectAsync("127.0.0.1", server.Port);
            var plc = new BenchPlc(modbus);

            await plc.KickWatchdogAsync();
            await plc.SetSetpointsAsync(900, 30);
            await plc.StartAsync();

            var status = await plc.ReadStatusAsync();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (status.SpeedRpm < 900 && DateTime.UtcNow < deadline)
            {
                await plc.KickWatchdogAsync();
                await Task.Delay(20);
                status = await plc.ReadStatusAsync();
            }

            Assert.Equal(BenchState.Running, status.State);
            Assert.Equal(900, status.SpeedRpm);
            Assert.Equal(900, status.SpeedSetpointRpm);
            Assert.Equal(30, status.LoadSetpointPct);
            Assert.True(status.DriveEnable);
            Assert.True(status.RunLamp);

            await plc.StopAsync();
            status = await plc.ReadStatusAsync();
            Assert.Equal(BenchState.Stopping, status.State);
        }
        finally
        {
            cts.Cancel();
            await scanLoop;
        }
    }

    [Fact]
    public async Task Command_times_out_when_nothing_runs_the_program()
    {
        var bank = new RegisterBank(); // no scan loop: Modbus answers, program doesn't run
        await using var server = new ModbusTcpServer(bank, IPAddress.Loopback, 0);
        server.Start();

        using var modbus = new ModbusTcpClient();
        await modbus.ConnectAsync("127.0.0.1", server.Port);
        var plc = new BenchPlc(modbus) { CommandTimeout = TimeSpan.FromMilliseconds(150) };

        await Assert.ThrowsAsync<TimeoutException>(() => plc.StartAsync());
    }

    [Fact]
    public void Alarm_tracker_reports_raise_and_clear()
    {
        var tracker = new AlarmTracker(filePath: null);
        var events = new List<AlarmEvent>();
        tracker.Changed += events.Add;

        var t = new DateTime(2026, 1, 1, 9, 0, 0);
        tracker.Update(StatusWith(t, AlarmCode.None));
        tracker.Update(StatusWith(t.AddSeconds(1), AlarmCode.EStop));
        tracker.Update(StatusWith(t.AddSeconds(2), AlarmCode.EStop));
        tracker.Update(StatusWith(t.AddSeconds(3), AlarmCode.None));

        Assert.Equal(2, events.Count);
        Assert.Equal(new AlarmEvent(t.AddSeconds(1), AlarmCode.EStop, Raised: true), events[0]);
        Assert.Equal(new AlarmEvent(t.AddSeconds(3), AlarmCode.EStop, Raised: false), events[1]);
    }

    private static BenchStatus StatusWith(DateTime t, AlarmCode alarm) =>
        new(t, alarm == AlarmCode.None ? BenchState.Idle : BenchState.Fault, 0, 0, 25, alarm, 0, 0, 0,
            false, false, false, alarm != AlarmCode.None);
}
