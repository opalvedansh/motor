using System.Diagnostics;
using System.Net;
using MotorBench.Modbus;
using MotorBench.Sim;

namespace MotorBench.Web;

/// <summary>
/// The MotorBench.Sim register bank + scan model, hosted in-process when no PLC
/// address is configured. The HMI still talks to it over a real Modbus TCP socket.
/// </summary>
public sealed class SimulatorHost : BackgroundService
{
    private readonly RegisterBank _bank = new();
    private readonly BenchModel _model = new();
    private readonly ModbusTcpServer _server;
    private volatile bool _paused;

    public SimulatorHost()
    {
        // started here, not in ExecuteAsync, so the port is known while wiring up the app
        _server = new ModbusTcpServer(_bank, IPAddress.Loopback, 0);
        _server.Start();
    }

    public int Port => _server.Port;

    /// <summary>Freezes the PLC scan while Modbus keeps answering, like a stopped PLC program. For fault drills and tests.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var scan = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        var clock = Stopwatch.StartNew();
        double last = 0;
        while (await scan.WaitForNextTickAsync(ct))
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Min(now - last, 0.1); // measured, the timer is not exact
            last = now;
            if (!_paused)
                _bank.Access((hr, coils) => _model.Scan(hr, coils, dt));
        }
    }

    public override void Dispose()
    {
        _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose();
    }
}
