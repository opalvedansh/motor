using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting.WindowsServices;
using MotorBench.Web;

// Browser HMI. Polls the PLC over Modbus TCP like the WinForms app, runs test
// sequences, and serves status to the page as JSON. See docs/web-hmi.md.
//
//   dotnet run --project src/MotorBench.Web                                         built-in simulator
//   dotnet run --project src/MotorBench.Web -- --plc-host 192.168.0.10 [--plc-port 502]   real PLC
//   dotnet run --project src/MotorBench.Web -- hash-password                          make a Users hash

if (args is ["hash-password", ..])
    return HashPasswordCommand();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // a Windows service starts in System32: read appsettings.json and wwwroot from the install folder
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});
// both are no-ops unless started by the service manager
builder.Services.AddWindowsService(o => o.ServiceName = "MotorBench");
builder.Services.AddSystemd();
var config = builder.Configuration;
config.AddCommandLine(args, new Dictionary<string, string>
{
    ["--plc-host"] = "Plc:Host",
    ["--plc-port"] = "Plc:Port",
    ["--data-dir"] = "DataDir",
});

string urls = config["urls"] ?? "http://localhost:5080"; // localhost only by default: this page can start a motor
var plc = PlcOptions.Load(config);
var users = Auth.LoadUsers(config);
Auth.CheckExposure(urls, users);

builder.WebHost.UseUrls(urls);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

int retentionDays = config.GetValue("DataRetentionDays", 365);
if (retentionDays < 0)
    throw new InvalidOperationException($"DataRetentionDays must be 0 (keep forever) or more, got {retentionDays}");
var settings = new WebSettings(
    Path.GetFullPath(config["DataDir"] ?? "data", builder.Environment.ContentRootPath),
    Path.Combine(AppContext.BaseDirectory, "profiles"),
    retentionDays);

PlcEndpoint endpoint;
if (!string.IsNullOrWhiteSpace(plc.Host))
{
    endpoint = new PlcEndpoint(plc.Host, plc.Port, $"{plc.Host}:{plc.Port}");
}
else
{
    // registered before BenchService so it starts first and stops last
    var sim = new SimulatorHost();
    builder.Services.AddSingleton(sim);
    builder.Services.AddHostedService(_ => sim);
    endpoint = new PlcEndpoint("127.0.0.1", sim.Port, $"built-in simulator (127.0.0.1:{sim.Port})");
}

builder.Services.AddSingleton(plc);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(endpoint);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ControlLock>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<BenchService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BenchService>());
builder.Services.AddHostedService<RetentionService>();
builder.Services.AddBenchAuth(users);

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles(); // the pages themselves are public; everything under /api needs a user
app.UseAuthentication();
if (users.Length == 0)
    app.UseLocalUser();
app.RequireClientHeader();
app.UseAudit();
app.UseAuthorization();
app.UseRateLimiter();
app.MapAuthApi(users);
app.MapBenchApi();

app.Logger.LogInformation("Motor Bench {Version}. PLC: {Plc}. Data folder: {DataDir}. {Login}", Api.Version, endpoint.DisplayName, settings.DataDir,
    users.Length == 0 ? "Local mode, no login (localhost only)." : $"{users.Length} user(s), login required.");
app.Run();
return 0;

static int HashPasswordCommand()
{
    string password;
    if (Console.IsInputRedirected)
    {
        password = Console.ReadLine() ?? "";
    }
    else
    {
        // read without echo; never take the password as an argument (shell history)
        Console.Error.Write("Password: ");
        var sb = new StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                    sb.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }
        Console.Error.WriteLine();
        password = sb.ToString();
    }

    if (password.Length < 8)
    {
        Console.Error.WriteLine("Password must be at least 8 characters.");
        return 1;
    }
    Console.WriteLine(PasswordHash.Create(password));
    return 0;
}

// lets the test project host this app with WebApplicationFactory
public partial class Program;
