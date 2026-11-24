using MotorBench.Plc;
using MotorBench.Sim;

namespace MotorBench.Tests;

// These go through the same scenarios as the checklist in plc/LADDER.md,
// against the C# copy of the PLC program.
public class BenchModelTests
{
    private const double Scan = 0.02;

    private readonly BenchModel _model = new();
    private readonly ushort[] _hr = new ushort[2048];
    private readonly bool[] _coils = new bool[64];
    private ushort _watchdog;

    [Fact]
    public void Start_ramps_to_setpoint_and_clears_the_command()
    {
        Setpoints(1500, 50);
        Command(RegisterMap.CmdStart);

        Assert.Equal(0, _hr[RegisterMap.Command]);
        Assert.Equal(BenchState.Running, _model.State);

        Run(3.0);
        Assert.Equal(1500, _hr[RegisterMap.SpeedActual]);
        Assert.True(_coils[RegisterMap.CoilDriveEnable]);
        // 50 % of 50 N·m + 0.8 friction + 0.0005 × 1500 = 26.55 N·m
        Assert.InRange(_hr[RegisterMap.TorqueActual], (ushort)265, (ushort)266);
    }

    [Fact]
    public void Setpoints_are_clamped()
    {
        Setpoints(5000, 150);
        Run(Scan);
        Assert.Equal(3000, _hr[RegisterMap.SpeedSetpointActive]);
        Assert.Equal(100, _hr[RegisterMap.LoadSetpointActive]);
    }

    [Fact]
    public void Stop_coasts_down_to_idle()
    {
        Setpoints(1200, 0);
        Command(RegisterMap.CmdStart);
        Run(2.5);

        Command(RegisterMap.CmdStop);
        Assert.Equal(BenchState.Stopping, _model.State);
        Assert.False(_coils[RegisterMap.CoilDriveEnable]);
        Assert.True(_coils[RegisterMap.CoilBrakeRelease]);

        Run(3.5);
        Assert.Equal(BenchState.Idle, _model.State);
        Assert.False(_coils[RegisterMap.CoilBrakeRelease]);
    }

    [Fact]
    public void EStop_trips_and_reset_needs_release_and_standstill()
    {
        Setpoints(1500, 0);
        Command(RegisterMap.CmdStart);
        Run(3.0);

        _hr[RegisterMap.SimEStop] = 1;
        Run(Scan);
        Assert.Equal(BenchState.Fault, _model.State);
        Assert.Equal(AlarmCode.EStop, _model.Alarm);
        Assert.False(_coils[RegisterMap.CoilDriveEnable]);
        Assert.True(_coils[RegisterMap.CoilFaultLamp]);

        _hr[RegisterMap.SimEStop] = 0;
        Command(RegisterMap.CmdReset);
        Assert.Equal(BenchState.Fault, _model.State); // still turning

        Run(4.5);
        Command(RegisterMap.CmdReset);
        Assert.Equal(BenchState.Idle, _model.State);
        Assert.Equal(AlarmCode.None, _model.Alarm);
    }

    [Fact]
    public void Start_is_ignored_while_faulted()
    {
        _hr[RegisterMap.SimEStop] = 1;
        Run(Scan);
        Command(RegisterMap.CmdStart);
        Assert.Equal(BenchState.Fault, _model.State);
        Assert.False(_coils[RegisterMap.CoilDriveEnable]);
    }

    [Fact]
    public void Watchdog_only_trips_while_running()
    {
        Run(3.0, kickWatchdog: false);
        Assert.Equal(BenchState.Idle, _model.State);
        Assert.Equal(AlarmCode.None, _model.Alarm);

        Setpoints(1000, 0);
        Command(RegisterMap.CmdStart);
        Run(1.5, kickWatchdog: false);
        Assert.Equal(BenchState.Running, _model.State);

        Run(0.6, kickWatchdog: false);
        Assert.Equal(AlarmCode.CommLoss, _model.Alarm);
        Assert.False(_coils[RegisterMap.CoilDriveEnable]);
    }

    [Fact]
    public void Overtemp_trips_at_full_load_and_needs_cooling_before_reset()
    {
        Setpoints(3000, 100);
        Command(RegisterMap.CmdStart);

        double t = 0;
        while (_model.State != BenchState.Fault && t < 120)
        {
            Run(0.1);
            t += 0.1;
        }
        Assert.Equal(AlarmCode.OverTemperature, _model.Alarm);
        Assert.InRange(t, 35.0, 60.0);

        for (int i = 0; i < 200 && _model.Speed >= 5; i++)
            Run(0.1);
        Assert.True(_model.Temp >= 110, $"expected still hot at standstill, was {_model.Temp:0.0}");

        Command(RegisterMap.CmdReset);
        Assert.Equal(BenchState.Fault, _model.State);

        for (int i = 0; i < 600 && _model.Temp >= 110; i++)
            Run(0.1);
        Command(RegisterMap.CmdReset);
        Assert.Equal(BenchState.Idle, _model.State);
    }

    [Fact]
    public void First_alarm_stays_in_the_alarm_register()
    {
        Setpoints(3000, 0);
        _hr[RegisterMap.SimRunaway] = 1;
        Command(RegisterMap.CmdStart);

        Run(6.0); // runs away towards 3600, trips at 3300
        Assert.Equal(AlarmCode.Overspeed, _model.Alarm);

        _hr[RegisterMap.SimEStop] = 1;
        Run(0.1);
        Assert.Equal(AlarmCode.Overspeed, _model.Alarm);
    }

    [Fact]
    public void Heartbeat_counts_scans()
    {
        Run(Scan * 10);
        Assert.Equal(10, _hr[RegisterMap.Heartbeat]);
    }

    private void Setpoints(int rpm, int load)
    {
        _hr[RegisterMap.SpeedSetpoint] = (ushort)rpm;
        _hr[RegisterMap.LoadSetpoint] = (ushort)load;
    }

    private void Command(ushort cmd)
    {
        _hr[RegisterMap.Command] = cmd;
        Run(Scan);
    }

    // the HMI polls every 100 ms = every 5 scans
    private void Run(double seconds, bool kickWatchdog = true)
    {
        int scans = (int)Math.Round(seconds / Scan);
        for (int i = 0; i < scans; i++)
        {
            if (kickWatchdog && i % 5 == 0)
                _hr[RegisterMap.HmiWatchdog] = ++_watchdog;
            _model.Scan(_hr, _coils, Scan);
        }
    }
}
