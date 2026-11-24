namespace MotorBench.Plc;

/// <summary>
/// Modbus addresses used by plc/bench_control.st, default OpenPLC v3 mapping
/// (%QX0.0 = coil 0, %QW0 = HR 0, %MW0 = HR 1024). See docs/design.md.
/// </summary>
public static class RegisterMap
{
    // coils, %QX0.0 .. %QX0.3
    public const ushort CoilDriveEnable = 0;
    public const ushort CoilBrakeRelease = 1;
    public const ushort CoilRunLamp = 2;
    public const ushort CoilFaultLamp = 3;
    public const ushort CoilCount = 4;

    // status, %QW0 .. %QW7
    public const ushort State = 0;
    public const ushort SpeedActual = 1;       // rpm
    public const ushort TorqueActual = 2;      // 0.1 N·m
    public const ushort MotorTemp = 3;         // 0.1 °C
    public const ushort Alarm = 4;
    public const ushort Heartbeat = 5;
    public const ushort SpeedSetpointActive = 6;
    public const ushort LoadSetpointActive = 7;
    public const ushort StatusCount = 8;

    // %QW8: which register map the PLC program implements. Bump CurrentMapVersion
    // whenever an address or its meaning changes, in the PLC program and here.
    public const ushort MapVersion = 8;
    public const ushort CurrentMapVersion = 1;

    // commands, %MW0 .. %MW5
    public const ushort Command = 1024;
    public const ushort SpeedSetpoint = 1025;
    public const ushort LoadSetpoint = 1026;
    public const ushort SimEStop = 1027;
    public const ushort HmiWatchdog = 1028;
    public const ushort SimRunaway = 1029;

    public const ushort CmdNone = 0;
    public const ushort CmdStart = 1;
    public const ushort CmdStop = 2;
    public const ushort CmdReset = 3;

    public const int MaxSpeedRpm = 3000;
    public const int MaxLoadPct = 100;
}
