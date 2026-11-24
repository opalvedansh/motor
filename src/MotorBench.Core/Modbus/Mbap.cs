using System.Buffers.Binary;

namespace MotorBench.Modbus;

/// <summary>
/// Modbus TCP header: transaction id (2), protocol id (2, always 0),
/// length (2, unit id + PDU), unit id (1).
/// </summary>
public static class Mbap
{
    public const int Length = 7;

    public static void Write(Span<byte> dst, ushort transactionId, int pduLength, byte unitId)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(dst[2..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(dst[4..], (ushort)(pduLength + 1));
        dst[6] = unitId;
    }

    public static (ushort TransactionId, ushort ProtocolId, int PduLength, byte UnitId) Read(ReadOnlySpan<byte> src)
    {
        ushort tid = BinaryPrimitives.ReadUInt16BigEndian(src);
        ushort protocol = BinaryPrimitives.ReadUInt16BigEndian(src[2..]);
        int length = BinaryPrimitives.ReadUInt16BigEndian(src[4..]);
        return (tid, protocol, length - 1, src[6]);
    }
}
