using System.Net.Sockets;

namespace MotorBench.Modbus;

/// <summary>
/// Small Modbus TCP client with the function codes this project needs
/// (FC01, FC03, FC06, FC16). Requests are sent one at a time.
/// </summary>
public sealed class ModbusTcpClient : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _transactionId;

    public byte UnitId { get; set; } = 1;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(1);
    public bool IsConnected => _stream is not null;

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        Close();
        var tcp = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout * 3);
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"Connecting to {host}:{port} timed out");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public void Close()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    // _gate is left alone on purpose: a request still in flight releases it after Close()
    public void Dispose() => Close();

    public async Task<bool[]> ReadCoilsAsync(ushort start, ushort count, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, (ushort)ModbusPdu.MaxReadCoils);
        var response = await TransactAsync(ModbusPdu.ReadRequest(ModbusPdu.ReadCoils, start, count), ct);
        return ModbusPdu.ParseCoils(response, count);
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort start, ushort count, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, (ushort)ModbusPdu.MaxReadRegisters);
        var response = await TransactAsync(ModbusPdu.ReadRequest(ModbusPdu.ReadHoldingRegisters, start, count), ct);
        return ModbusPdu.ParseRegisters(response, count);
    }

    public async Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken ct = default)
    {
        await TransactAsync(ModbusPdu.WriteSingleRegisterRequest(address, value), ct);
    }

    public async Task WriteMultipleRegistersAsync(ushort start, ushort[] values, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(values.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(values.Length, ModbusPdu.MaxWriteRegisters);
        await TransactAsync(ModbusPdu.WriteMultipleRegistersRequest(start, values), ct);
    }

    private async Task<byte[]> TransactAsync(byte[] pdu, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var stream = _stream ?? throw new InvalidOperationException("Not connected");
            ushort tid = unchecked(++_transactionId);

            var frame = new byte[Mbap.Length + pdu.Length];
            Mbap.Write(frame, tid, pdu.Length, UnitId);
            pdu.CopyTo(frame, Mbap.Length);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            try
            {
                await stream.WriteAsync(frame, cts.Token);

                var header = new byte[Mbap.Length];
                await stream.ReadExactlyAsync(header, cts.Token);
                var (rxTid, protocol, pduLength, _) = Mbap.Read(header);
                if (protocol != 0 || pduLength < 1 || pduLength > 253)
                    throw new IOException($"Bad MBAP header (protocol {protocol}, length {pduLength + 1})");

                var response = new byte[pduLength];
                await stream.ReadExactlyAsync(response, cts.Token);
                if (rxTid != tid)
                    throw new IOException($"Transaction id mismatch (sent {tid}, got {rxTid})");

                ModbusPdu.ThrowIfException(pdu[0], response);
                return response;
            }
            catch (OperationCanceledException)
            {
                // The reply may still arrive later and would be read as the
                // answer to the next request, so drop the connection.
                Close();
                if (ct.IsCancellationRequested)
                    throw;
                throw new TimeoutException($"No reply within {Timeout.TotalMilliseconds:0} ms");
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                Close();
                if (ex is IOException)
                    throw;
                throw new IOException("Connection lost", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
