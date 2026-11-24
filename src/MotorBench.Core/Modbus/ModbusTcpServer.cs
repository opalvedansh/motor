using System.Net;
using System.Net.Sockets;

namespace MotorBench.Modbus;

/// <summary>Modbus TCP server backed by a <see cref="RegisterBank"/>.</summary>
public sealed class ModbusTcpServer : IAsyncDisposable
{
    private readonly RegisterBank _bank;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public ModbusTcpServer(RegisterBank bank, IPAddress address, int port)
    {
        _bank = bank;
        _listener = new TcpListener(address, port);
    }

    /// <summary>Actual port, useful when started with port 0.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
            await _acceptLoop;
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                break;
            }
            _ = ServeAsync(client, ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var header = new byte[Mbap.Length];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header, ct);
                    var (tid, protocol, pduLength, unit) = Mbap.Read(header);
                    if (protocol != 0 || pduLength < 1 || pduLength > 253)
                        return;

                    var request = new byte[pduLength];
                    await stream.ReadExactlyAsync(request, ct);

                    var response = _bank.Handle(request);
                    var frame = new byte[Mbap.Length + response.Length];
                    Mbap.Write(frame, tid, response.Length, unit);
                    response.CopyTo(frame, Mbap.Length);
                    await stream.WriteAsync(frame, ct);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // client went away or server is stopping
            }
        }
    }
}
