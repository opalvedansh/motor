using System.Buffers.Binary;

namespace MotorBench.Modbus;

/// <summary>
/// Building request PDUs and parsing responses. Kept separate from the socket
/// code so it can be unit tested byte by byte.
/// </summary>
public static class ModbusPdu
{
    public const byte ReadCoils = 0x01;
    public const byte ReadHoldingRegisters = 0x03;
    public const byte WriteSingleCoil = 0x05;
    public const byte WriteSingleRegister = 0x06;
    public const byte WriteMultipleRegisters = 0x10;

    public const byte IllegalFunction = 0x01;
    public const byte IllegalDataAddress = 0x02;
    public const byte IllegalDataValue = 0x03;
    public const byte ServerDeviceFailure = 0x04;

    public const int MaxReadCoils = 2000;
    public const int MaxReadRegisters = 125;
    public const int MaxWriteRegisters = 123;

    public static byte[] ReadRequest(byte function, ushort start, ushort count)
    {
        var pdu = new byte[5];
        pdu[0] = function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), count);
        return pdu;
    }

    public static byte[] WriteSingleRegisterRequest(ushort address, ushort value)
    {
        var pdu = new byte[5];
        pdu[0] = WriteSingleRegister;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value);
        return pdu;
    }

    public static byte[] WriteMultipleRegistersRequest(ushort start, ReadOnlySpan<ushort> values)
    {
        var pdu = new byte[6 + values.Length * 2];
        pdu[0] = WriteMultipleRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
        pdu[5] = (byte)(values.Length * 2);
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + i * 2), values[i]);
        return pdu;
    }

    /// <summary>Throws if the response is an exception response (function code | 0x80).</summary>
    public static void ThrowIfException(byte requestFunction, ReadOnlySpan<byte> response)
    {
        if (response.Length >= 2 && response[0] == (requestFunction | 0x80))
            throw new ModbusException(requestFunction, response[1]);
        if (response.Length == 0 || response[0] != requestFunction)
            throw new IOException($"Unexpected function code in response (sent 0x{requestFunction:X2})");
    }

    public static bool[] ParseCoils(ReadOnlySpan<byte> response, int count)
    {
        int byteCount = (count + 7) / 8;
        if (response.Length != 2 + byteCount || response[1] != byteCount)
            throw new IOException("Bad length in read coils response");

        // coil 0 is the least significant bit of the first byte
        var bits = new bool[count];
        for (int i = 0; i < count; i++)
            bits[i] = (response[2 + i / 8] & (1 << (i % 8))) != 0;
        return bits;
    }

    public static ushort[] ParseRegisters(ReadOnlySpan<byte> response, int count)
    {
        if (response.Length != 2 + count * 2 || response[1] != count * 2)
            throw new IOException("Bad length in read registers response");

        var values = new ushort[count];
        for (int i = 0; i < count; i++)
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(response[(2 + i * 2)..]);
        return values;
    }
}
