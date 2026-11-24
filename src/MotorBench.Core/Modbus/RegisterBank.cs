using System.Buffers.Binary;

namespace MotorBench.Modbus;

/// <summary>
/// Coils and holding registers for the Modbus server side (used by the
/// simulator and the tests). All access goes through one lock, the same way
/// OpenPLC locks its I/O buffers while the program scan runs.
/// </summary>
public sealed class RegisterBank
{
    private readonly object _sync = new();
    private readonly ushort[] _holding;
    private readonly bool[] _coils;

    public RegisterBank(int holdingCount = 2048, int coilCount = 64)
    {
        _holding = new ushort[holdingCount];
        _coils = new bool[coilCount];
    }

    public void Access(Action<ushort[], bool[]> action)
    {
        lock (_sync)
            action(_holding, _coils);
    }

    public ushort GetHolding(int address)
    {
        lock (_sync)
            return _holding[address];
    }

    public void SetHolding(int address, ushort value)
    {
        lock (_sync)
            _holding[address] = value;
    }

    public bool GetCoil(int address)
    {
        lock (_sync)
            return _coils[address];
    }

    public void SetCoil(int address, bool value)
    {
        lock (_sync)
            _coils[address] = value;
    }

    /// <summary>Handles one request PDU and returns the response PDU.</summary>
    public byte[] Handle(ReadOnlySpan<byte> request)
    {
        if (request.Length == 0)
            return Error(0, ModbusPdu.IllegalFunction);

        byte function = request[0];
        if (request.Length < 5)
            return Error(function, ModbusPdu.IllegalDataValue);

        ushort address = BinaryPrimitives.ReadUInt16BigEndian(request[1..]);
        ushort word = BinaryPrimitives.ReadUInt16BigEndian(request[3..]);

        lock (_sync)
        {
            switch (function)
            {
                case ModbusPdu.ReadCoils:
                    return ReadCoils(function, address, word);
                case ModbusPdu.ReadHoldingRegisters:
                    return ReadHolding(function, address, word);
                case ModbusPdu.WriteSingleCoil:
                    if (address >= _coils.Length)
                        return Error(function, ModbusPdu.IllegalDataAddress);
                    if (word is not (0x0000 or 0xFF00))
                        return Error(function, ModbusPdu.IllegalDataValue);
                    _coils[address] = word == 0xFF00;
                    return request[..5].ToArray();
                case ModbusPdu.WriteSingleRegister:
                    if (address >= _holding.Length)
                        return Error(function, ModbusPdu.IllegalDataAddress);
                    _holding[address] = word;
                    return request[..5].ToArray();
                case ModbusPdu.WriteMultipleRegisters:
                    return WriteMultiple(function, address, word, request);
                default:
                    return Error(function, ModbusPdu.IllegalFunction);
            }
        }
    }

    private byte[] ReadCoils(byte function, int start, int count)
    {
        if (count is < 1 or > ModbusPdu.MaxReadCoils)
            return Error(function, ModbusPdu.IllegalDataValue);
        if (start + count > _coils.Length)
            return Error(function, ModbusPdu.IllegalDataAddress);

        int byteCount = (count + 7) / 8;
        var response = new byte[2 + byteCount];
        response[0] = function;
        response[1] = (byte)byteCount;
        for (int i = 0; i < count; i++)
        {
            if (_coils[start + i])
                response[2 + i / 8] |= (byte)(1 << (i % 8));
        }
        return response;
    }

    private byte[] ReadHolding(byte function, int start, int count)
    {
        if (count is < 1 or > ModbusPdu.MaxReadRegisters)
            return Error(function, ModbusPdu.IllegalDataValue);
        if (start + count > _holding.Length)
            return Error(function, ModbusPdu.IllegalDataAddress);

        var response = new byte[2 + count * 2];
        response[0] = function;
        response[1] = (byte)(count * 2);
        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2 + i * 2), _holding[start + i]);
        return response;
    }

    private byte[] WriteMultiple(byte function, int start, int count, ReadOnlySpan<byte> request)
    {
        if (count is < 1 or > ModbusPdu.MaxWriteRegisters || request.Length < 6
            || request[5] != count * 2 || request.Length != 6 + count * 2)
            return Error(function, ModbusPdu.IllegalDataValue);
        if (start + count > _holding.Length)
            return Error(function, ModbusPdu.IllegalDataAddress);

        for (int i = 0; i < count; i++)
            _holding[start + i] = BinaryPrimitives.ReadUInt16BigEndian(request[(6 + i * 2)..]);
        return request[..5].ToArray();
    }

    private static byte[] Error(byte function, byte code) => [(byte)(function | 0x80), code];
}
