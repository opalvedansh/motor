using System.ComponentModel.DataAnnotations;

namespace MotorBench.Web;

/// <summary>"Plc" section of the configuration. Validated at startup.</summary>
public sealed class PlcOptions
{
    /// <summary>PLC address. Empty: run the built-in simulator.</summary>
    public string? Host { get; set; }

    [Range(1, 65535)]
    public int Port { get; set; } = 502;

    [Range(0, 255)]
    public int UnitId { get; set; } = 1;

    /// <summary>Status poll and watchdog period. Must stay well below the PLC's 2 s watchdog.</summary>
    [Range(50, 1000)]
    public int PollIntervalMs { get; set; } = 100;

    /// <summary>Reply timeout for one Modbus request.</summary>
    [Range(100, 10000)]
    public int TimeoutMs { get; set; } = 1000;

    /// <summary>How long the PLC may take to accept a start/stop/reset command.</summary>
    [Range(100, 10000)]
    public int CommandTimeoutMs { get; set; } = 500;

    public static PlcOptions Load(IConfiguration config)
    {
        var options = config.GetSection("Plc").Get<PlcOptions>() ?? new PlcOptions();
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(options, new ValidationContext(options), errors, validateAllProperties: true))
            throw new InvalidOperationException("Invalid Plc configuration: " + string.Join("; ", errors.Select(e => e.ErrorMessage)));
        return options;
    }
}
