using MotorBench.Modbus;

namespace MotorBench.Plc;

/// <summary>What the sequence runner needs from the bench. Faked in tests.</summary>
public interface IBenchPlc
{
    Task<BenchStatus> ReadStatusAsync(CancellationToken ct = default);
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task ResetAsync(CancellationToken ct = default);
    Task SetSetpointsAsync(int speedRpm, int loadPct, CancellationToken ct = default);
}

public sealed class BenchPlc : IBenchPlc
{
    private readonly ModbusTcpClient _modbus;
    private ushort _watchdog;

    public BenchPlc(ModbusTcpClient modbus)
    {
        _modbus = modbus;
    }

    /// <summary>How long to wait for the PLC to take a command (it clears CMD to 0).</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

    public async Task<BenchStatus> ReadStatusAsync(CancellationToken ct = default)
    {
        var regs = await _modbus.ReadHoldingRegistersAsync(RegisterMap.State, RegisterMap.StatusCount, ct);
        var coils = await _modbus.ReadCoilsAsync(RegisterMap.CoilDriveEnable, RegisterMap.CoilCount, ct);
        return BenchStatus.FromRegisters(DateTime.Now, regs, coils);
    }

    /// <summary>Which register map the PLC program implements, see <see cref="RegisterMap.CurrentMapVersion"/>.</summary>
    public async Task<int> ReadMapVersionAsync(CancellationToken ct = default) =>
        (short)(await _modbus.ReadHoldingRegistersAsync(RegisterMap.MapVersion, 1, ct))[0];

    public Task StartAsync(CancellationToken ct = default) => SendCommandAsync(RegisterMap.CmdStart, ct);

    public Task StopAsync(CancellationToken ct = default) => SendCommandAsync(RegisterMap.CmdStop, ct);

    public Task ResetAsync(CancellationToken ct = default) => SendCommandAsync(RegisterMap.CmdReset, ct);

    public Task SetSetpointsAsync(int speedRpm, int loadPct, CancellationToken ct = default)
    {
        // the PLC clamps as well, this just keeps obviously wrong values off the wire
        ushort speed = (ushort)Math.Clamp(speedRpm, 0, RegisterMap.MaxSpeedRpm);
        ushort load = (ushort)Math.Clamp(loadPct, 0, RegisterMap.MaxLoadPct);
        return _modbus.WriteMultipleRegistersAsync(RegisterMap.SpeedSetpoint, [speed, load], ct);
    }

    public Task SetSimEStopAsync(bool pressed, CancellationToken ct = default) =>
        _modbus.WriteSingleRegisterAsync(RegisterMap.SimEStop, pressed ? (ushort)1 : (ushort)0, ct);

    public Task SetSimRunawayAsync(bool on, CancellationToken ct = default) =>
        _modbus.WriteSingleRegisterAsync(RegisterMap.SimRunaway, on ? (ushort)1 : (ushort)0, ct);

    /// <summary>Call on every poll. The PLC faults the bench if this stops changing while running.</summary>
    public Task KickWatchdogAsync(CancellationToken ct = default)
    {
        _watchdog = (ushort)((_watchdog + 1) % 30000);
        return _modbus.WriteSingleRegisterAsync(RegisterMap.HmiWatchdog, _watchdog, ct);
    }

    private async Task SendCommandAsync(ushort command, CancellationToken ct)
    {
        await _modbus.WriteSingleRegisterAsync(RegisterMap.Command, command, ct);

        // The PLC reads CMD once per scan and writes 0 back. If it is still
        // there after a few scans the PLC is not running the program.
        var deadline = DateTime.UtcNow + CommandTimeout;
        while (true)
        {
            await Task.Delay(20, ct);
            var cmd = await _modbus.ReadHoldingRegistersAsync(RegisterMap.Command, 1, ct);
            if (cmd[0] == RegisterMap.CmdNone)
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"PLC did not take command {command} (is the program running?)");
        }
    }
}
