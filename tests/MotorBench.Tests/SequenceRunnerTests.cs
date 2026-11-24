using MotorBench.Plc;
using MotorBench.Sequencing;

namespace MotorBench.Tests;

public class SequenceRunnerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 9, 0, 0);

    private readonly FakePlc _plc = new();
    private double _t;

    [Fact]
    public async Task Runs_all_steps_then_stops_the_bench()
    {
        var runner = new SequenceRunner(_plc);
        var profile = TestProfile.Parse("p", ["1000,20,2", "2000,40,3"]);

        await runner.StartAsync(profile, Status(BenchState.Idle, 0));
        Assert.Equal(new[] { "sp 1000 20", "start" }, _plc.Calls);

        await FeedWhileActive(runner, 60);

        Assert.Equal(RunnerState.Completed, runner.State);
        Assert.Equal(new[] { "sp 1000 20", "start", "sp 2000 40", "stop" }, _plc.Calls);
        Assert.Equal(2, runner.Results.Count);
        Assert.InRange(runner.Results[0].SettleSeconds, 0.9, 1.2);
        Assert.Equal(2000.0, runner.Results[1].AvgSpeedRpm);
        // 1 s settle + 2 s dwell + 1 s settle + 3 s dwell, plus a tick or two of rounding
        Assert.InRange(_t, 7.0, 7.6);
    }

    [Fact]
    public async Task Hold_freezes_the_timers()
    {
        var runner = new SequenceRunner(_plc);
        await runner.StartAsync(TestProfile.Parse("p", ["1000,0,2"]), Status(BenchState.Idle, 0));

        await Feed(runner, 1.5);
        Assert.Equal(RunnerState.Dwelling, runner.State);

        runner.Hold();
        await Feed(runner, 5.0);
        Assert.Equal(RunnerState.Dwelling, runner.State);
        Assert.True(runner.IsHeld);
        Assert.True(runner.DwellElapsed < 1.0);

        runner.Resume();
        await FeedWhileActive(runner, 10);
        Assert.Equal(RunnerState.Completed, runner.State);
        Assert.Equal(new[] { "sp 1000 0", "start", "stop" }, _plc.Calls);
    }

    [Fact]
    public async Task Bench_alarm_aborts_without_sending_stop()
    {
        var runner = new SequenceRunner(_plc);
        await runner.StartAsync(TestProfile.Parse("p", ["1000,0,5"]), Status(BenchState.Idle, 0));
        await Feed(runner, 0.5);

        _t += 0.1;
        await runner.UpdateAsync(Status(BenchState.Fault, 800, AlarmCode.OverTemperature));

        Assert.Equal(RunnerState.Aborted, runner.State);
        Assert.Contains(BenchText.Describe(AlarmCode.OverTemperature), runner.AbortReason);
        Assert.DoesNotContain("stop", _plc.Calls);
    }

    [Fact]
    public async Task Settle_timeout_aborts_and_stops()
    {
        var runner = new SequenceRunner(_plc) { SettleTimeout = TimeSpan.FromSeconds(5) };
        await runner.StartAsync(TestProfile.Parse("p", ["2000,0,2"]), Status(BenchState.Idle, 0));

        // bench never gets closer than 1500 rpm
        for (int i = 0; i < 60 && runner.IsActive; i++)
        {
            _t += 0.1;
            await runner.UpdateAsync(Status(BenchState.Running, 1500));
        }

        Assert.Equal(RunnerState.Aborted, runner.State);
        Assert.Equal("stop", _plc.Calls[^1]);
    }

    [Fact]
    public async Task Cannot_start_while_faulted()
    {
        var runner = new SequenceRunner(_plc);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.StartAsync(TestProfile.Parse("p", ["1000,0,2"]), Status(BenchState.Fault, 0, AlarmCode.EStop)));
        Assert.Empty(_plc.Calls);
    }

    [Fact]
    public async Task Start_the_plc_does_not_take_aborts_the_run()
    {
        var runner = new SequenceRunner(_plc);
        _plc.StartFails = true;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            runner.StartAsync(TestProfile.Parse("p", ["1000,0,2"]), Status(BenchState.Idle, 0)));

        Assert.Equal(RunnerState.Aborted, runner.State);
        Assert.StartsWith("start failed", runner.AbortReason);
        Assert.False(runner.IsActive);
    }

    [Fact]
    public async Task Bench_stopped_outside_the_sequence_aborts_it()
    {
        var runner = new SequenceRunner(_plc);
        await runner.StartAsync(TestProfile.Parse("p", ["1000,0,5"]), Status(BenchState.Idle, 0));
        await Feed(runner, 0.5);

        // someone pressed Stop on the panel or in another HMI
        _t += 0.1;
        await runner.UpdateAsync(Status(BenchState.Stopping, 900));

        Assert.Equal(RunnerState.Aborted, runner.State);
        Assert.Equal("bench was stopped outside the sequence", runner.AbortReason);
    }

    // bench follows the last setpoint instantly
    private async Task Feed(SequenceRunner runner, double seconds)
    {
        int ticks = (int)Math.Round(seconds / 0.1);
        for (int i = 0; i < ticks; i++)
        {
            _t += 0.1;
            await runner.UpdateAsync(Status(BenchState.Running, _plc.SpeedSetpoint));
        }
    }

    private async Task FeedWhileActive(SequenceRunner runner, double maxSeconds)
    {
        while (runner.IsActive && _t < maxSeconds)
        {
            _t += 0.1;
            await runner.UpdateAsync(Status(BenchState.Running, _plc.SpeedSetpoint));
        }
    }

    private BenchStatus Status(BenchState state, int speed, AlarmCode alarm = AlarmCode.None) =>
        new(T0.AddSeconds(_t), state, speed, TorqueNm: 10, MotorTempC: 40, alarm, Heartbeat: 0,
            SpeedSetpointRpm: _plc.SpeedSetpoint, LoadSetpointPct: 0,
            DriveEnable: state == BenchState.Running, BrakeRelease: false, RunLamp: false, FaultLamp: false);

    private sealed class FakePlc : IBenchPlc
    {
        public List<string> Calls { get; } = [];
        public int SpeedSetpoint { get; private set; }

        public Task<BenchStatus> ReadStatusAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public bool StartFails { get; set; }

        public Task StartAsync(CancellationToken ct = default) =>
            StartFails ? Task.FromException(new TimeoutException("PLC did not take command 1")) : Record("start");
        public Task StopAsync(CancellationToken ct = default) => Record("stop");
        public Task ResetAsync(CancellationToken ct = default) => Record("reset");

        public Task SetSetpointsAsync(int speedRpm, int loadPct, CancellationToken ct = default)
        {
            SpeedSetpoint = speedRpm;
            return Record($"sp {speedRpm} {loadPct}");
        }

        private Task Record(string call)
        {
            Calls.Add(call);
            return Task.CompletedTask;
        }
    }
}
