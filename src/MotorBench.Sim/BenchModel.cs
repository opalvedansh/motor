using MotorBench.Plc;

namespace MotorBench.Sim;

/// <summary>
/// C# copy of plc/bench_control.st (program logic + BenchSim FB) working on
/// the same register map, so the HMI can be run without OpenPLC.
/// The PLC program is the reference. If you change one, change the other.
/// </summary>
public sealed class BenchModel
{
    // BenchSim constants
    private const double Accel = 600.0;
    private const double Decel = 900.0;
    private const double Coast = 400.0;
    private const double RunawayRpm = 3600.0;
    private const double RatedTorque = 50.0;
    private const double Friction = 0.8;
    private const double Viscous = 0.0005;
    private const double Ambient = 25.0;
    private const double KCopper = 0.04;
    private const double KIron = 0.005;
    private const double Tau = 30.0;

    // bench_main constants
    private const double OverspeedRpm = 3300.0;
    private const double OvertempTrip = 120.0;
    private const double OvertempReset = 110.0;
    private const double StandstillRpm = 5.0;
    private const double WatchdogTimeout = 2.0;

    private int _state;
    private int _alarm;
    private int _heartbeat;
    private ushort _wdLast;
    private double _wdElapsed;

    public double Speed { get; private set; }
    public double Torque { get; private set; }
    public double Temp { get; private set; } = Ambient;
    public BenchState State => (BenchState)_state;
    public AlarmCode Alarm => (AlarmCode)_alarm;

    /// <summary>One PLC scan. <paramref name="dt"/> is the scan time in seconds.</summary>
    public void Scan(ushort[] hr, bool[] coils, double dt)
    {
        // 1. commands
        ushort cmd = hr[RegisterMap.Command];
        bool startReq = cmd == RegisterMap.CmdStart;
        bool stopReq = cmd == RegisterMap.CmdStop;
        bool resetReq = cmd == RegisterMap.CmdReset;
        hr[RegisterMap.Command] = RegisterMap.CmdNone;

        int speedSp = Math.Clamp((int)(short)hr[RegisterMap.SpeedSetpoint], 0, RegisterMap.MaxSpeedRpm);
        int loadSp = Math.Clamp((int)(short)hr[RegisterMap.LoadSetpoint], 0, RegisterMap.MaxLoadPct);

        // 2. watchdog (TON with IN = not changed)
        ushort wd = hr[RegisterMap.HmiWatchdog];
        _wdElapsed = wd != _wdLast ? 0 : _wdElapsed + dt;
        _wdLast = wd;
        bool wdExpired = _wdElapsed >= WatchdogTimeout;

        // 3. alarms
        bool standstill = Speed < StandstillRpm;
        bool estop = hr[RegisterMap.SimEStop] != 0;
        bool overspeed = Speed > OverspeedRpm;
        bool overtemp = Temp > OvertempTrip;
        bool commloss = wdExpired && (_state == 1 || _state == 2);

        if (_alarm == 0)
        {
            if (estop) _alarm = 1;
            else if (overspeed) _alarm = 2;
            else if (overtemp) _alarm = 3;
            else if (commloss) _alarm = 4;
        }

        bool resetBlocked = estop || overspeed || overtemp || (_alarm == 3 && Temp >= OvertempReset);

        // 4. state machine
        if (_alarm != 0)
            _state = 3;

        switch (_state)
        {
            case 0:
                if (startReq) _state = 1;
                break;
            case 1:
                if (stopReq) _state = 2;
                break;
            case 2:
                if (startReq) _state = 1;
                else if (standstill) _state = 0;
                break;
            case 3:
                if (resetReq && !resetBlocked && standstill)
                {
                    _alarm = 0;
                    _state = 0;
                }
                break;
            default:
                _state = 0;
                break;
        }

        // 5. outputs
        bool driveEnable = _state == 1;
        coils[RegisterMap.CoilDriveEnable] = driveEnable;
        coils[RegisterMap.CoilBrakeRelease] = driveEnable || !standstill;
        coils[RegisterMap.CoilRunLamp] = _state == 1;
        coils[RegisterMap.CoilFaultLamp] = _state == 3;

        // 6. simulated bench
        StepPlant(driveEnable, speedSp, loadSp, hr[RegisterMap.SimRunaway] != 0, dt);

        // 7. status
        _heartbeat = _heartbeat >= 29999 ? 0 : _heartbeat + 1;

        hr[RegisterMap.State] = (ushort)_state;
        hr[RegisterMap.SpeedActual] = ToReg(Speed);
        hr[RegisterMap.TorqueActual] = ToReg(Torque * 10.0);
        hr[RegisterMap.MotorTemp] = ToReg(Temp * 10.0);
        hr[RegisterMap.Alarm] = (ushort)_alarm;
        hr[RegisterMap.Heartbeat] = (ushort)_heartbeat;
        hr[RegisterMap.SpeedSetpointActive] = (ushort)speedSp;
        hr[RegisterMap.LoadSetpointActive] = (ushort)loadSp;
        hr[RegisterMap.MapVersion] = RegisterMap.CurrentMapVersion;
    }

    private void StepPlant(bool enable, double speedSp, double loadPct, bool runaway, double dt)
    {
        double target = enable ? (runaway ? RunawayRpm : speedSp) : 0.0;

        if (Speed < target)
            Speed = Math.Min(Speed + Accel * dt, target);
        else if (enable)
            Speed = Math.Max(Speed - Decel * dt, target);
        else
            Speed = Math.Max(Speed - Coast * dt, 0.0);

        Torque = Speed > 1.0 ? loadPct * 0.01 * RatedTorque + Friction + Viscous * Speed : 0.0;

        double rise = KCopper * Torque * Torque + KIron * Speed;
        Temp += (Ambient + rise - Temp) * dt / Tau;
    }

    // REAL_TO_INT rounds; the register holds the INT as two's complement
    private static ushort ToReg(double value) => unchecked((ushort)(short)Math.Round(value));
}
