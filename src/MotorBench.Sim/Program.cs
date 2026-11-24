using System.Diagnostics;
using System.Globalization;
using System.Net;
using MotorBench.Modbus;
using MotorBench.Sim;

// Stand-in for OpenPLC + bench_control.st during HMI development.
// Usage: MotorBench.Sim [port]   (default 502, same as OpenPLC)

int port = 502;
if (args.Length > 0 && !int.TryParse(args[0], out port))
{
    Console.Error.WriteLine("usage: MotorBench.Sim [port]");
    return 1;
}

var bank = new RegisterBank();
var model = new BenchModel();

await using var server = new ModbusTcpServer(bank, IPAddress.Loopback, port);
try
{
    server.Start();
}
catch (System.Net.Sockets.SocketException ex)
{
    // macOS/Linux only let root bind ports below 1024
    Console.Error.WriteLine($"Cannot listen on port {port}: {ex.Message}. Try a port above 1024, e.g. MotorBench.Sim 5020");
    return 1;
}
Console.WriteLine($"Bench simulator on 127.0.0.1:{server.Port}. Ctrl+C to quit.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Use measured time instead of a fixed 20 ms: on Windows the timer only
// ticks every ~15.6 ms, so a fixed dt would make the simulation run slow.
using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
var clock = Stopwatch.StartNew();
double last = 0;
double lastPrint = 0;

try
{
    while (await timer.WaitForNextTickAsync(cts.Token))
    {
        double now = clock.Elapsed.TotalSeconds;
        double dt = Math.Min(now - last, 0.1);
        last = now;

        bank.Access((hr, coils) => model.Scan(hr, coils, dt));

        if (now - lastPrint >= 1.0)
        {
            lastPrint = now;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.Now:HH:mm:ss}  {model.State,-8} {model.Speed,6:0} rpm {model.Torque,6:0.0} Nm " +
                $"{model.Temp,6:0.0} C  alarm {(int)model.Alarm}"));
        }
    }
}
catch (OperationCanceledException)
{
}

return 0;
