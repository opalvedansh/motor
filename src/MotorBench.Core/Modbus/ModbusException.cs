namespace MotorBench.Modbus;

/// <summary>The server answered with a Modbus exception response.</summary>
public sealed class ModbusException : Exception
{
    public ModbusException(byte function, byte exceptionCode)
        : base($"Modbus exception {exceptionCode} ({Describe(exceptionCode)}), function 0x{function:X2}")
    {
        Function = function;
        ExceptionCode = exceptionCode;
    }

    public byte Function { get; }
    public byte ExceptionCode { get; }

    private static string Describe(byte code) => code switch
    {
        ModbusPdu.IllegalFunction => "illegal function",
        ModbusPdu.IllegalDataAddress => "illegal data address",
        ModbusPdu.IllegalDataValue => "illegal data value",
        ModbusPdu.ServerDeviceFailure => "server device failure",
        _ => "unknown",
    };
}
