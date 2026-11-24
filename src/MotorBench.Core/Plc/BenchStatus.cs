namespace MotorBench.Plc;

public sealed record BenchStatus(
    DateTime Timestamp,
    BenchState State,
    int SpeedRpm,
    double TorqueNm,
    double MotorTempC,
    AlarmCode Alarm,
    int Heartbeat,
    int SpeedSetpointRpm,
    int LoadSetpointPct,
    bool DriveEnable,
    bool BrakeRelease,
    bool RunLamp,
    bool FaultLamp)
{
    public static BenchStatus FromRegisters(DateTime timestamp, IReadOnlyList<ushort> regs, IReadOnlyList<bool> coils)
    {
        if (regs.Count < RegisterMap.StatusCount || coils.Count < RegisterMap.CoilCount)
            throw new ArgumentException("Not enough registers for a status read");

        // PLC side declares these as INT, so read them back as signed
        static int S(ushort v) => (short)v;

        return new BenchStatus(
            timestamp,
            (BenchState)S(regs[RegisterMap.State]),
            S(regs[RegisterMap.SpeedActual]),
            S(regs[RegisterMap.TorqueActual]) / 10.0,
            S(regs[RegisterMap.MotorTemp]) / 10.0,
            (AlarmCode)S(regs[RegisterMap.Alarm]),
            S(regs[RegisterMap.Heartbeat]),
            S(regs[RegisterMap.SpeedSetpointActive]),
            S(regs[RegisterMap.LoadSetpointActive]),
            coils[RegisterMap.CoilDriveEnable],
            coils[RegisterMap.CoilBrakeRelease],
            coils[RegisterMap.CoilRunLamp],
            coils[RegisterMap.CoilFaultLamp]);
    }
}
