using System.Globalization;

namespace MotorBench.Web;

public sealed record RunSummary(
    string Report, string Profile, string? DutSerial, string? Operator, string Outcome, DateTime Finished, int Steps, string? RawData);

/// <summary>
/// Run files in the data folder: <c>result_*.csv</c> (step report with a <c># key,value</c>
/// header) and <c>raw_*.csv</c> (one row per poll). Audit and alarm files are not run files.
/// </summary>
public static class RunFiles
{
    public static bool IsRunFile(string name) =>
        name == Path.GetFileName(name)
        && name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
        && (name.StartsWith("result_", StringComparison.Ordinal) || name.StartsWith("raw_", StringComparison.Ordinal));

    /// <summary>Newest first. Reads only the small report files, never the raw data.</summary>
    public static List<RunSummary> List(string dataDir, int max)
    {
        if (!Directory.Exists(dataDir))
            return [];
        var runs = new List<RunSummary>();
        foreach (var file in Directory.EnumerateFiles(dataDir, "result_*.csv"))
        {
            try
            {
                runs.Add(Read(file));
            }
            catch (IOException)
            {
                // being written or deleted right now; it shows up on the next refresh
            }
        }
        return [.. runs.OrderByDescending(r => r.Finished).Take(max)];
    }

    private static RunSummary Read(string file)
    {
        var info = new Dictionary<string, string>();
        int steps = 0;
        foreach (var line in File.ReadLines(file))
        {
            int comma = line.IndexOf(',');
            if (line.StartsWith("# ") && comma > 2)
                info[line[2..comma]] = line[(comma + 1)..];
            else if (line.Length > 0 && char.IsAsciiDigit(line[0]))
                steps++;
        }

        if (!DateTime.TryParseExact(info.GetValueOrDefault("finished"), "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var finished))
            finished = File.GetLastWriteTime(file);

        return new RunSummary(
            Path.GetFileName(file),
            info.GetValueOrDefault("profile") ?? "?",
            info.GetValueOrDefault("dut_serial"), // older files (WinForms HMI) have none
            info.GetValueOrDefault("operator"),
            info.GetValueOrDefault("outcome") ?? "?",
            finished,
            steps,
            info.GetValueOrDefault("raw_data"));
    }

    /// <summary>Deletes run files last written more than <paramref name="retentionDays"/> ago. 0 keeps everything.</summary>
    public static int Prune(string dataDir, int retentionDays, DateTime now)
    {
        if (retentionDays <= 0 || !Directory.Exists(dataDir))
            return 0;
        int deleted = 0;
        foreach (var file in Directory.EnumerateFiles(dataDir, "*.csv"))
        {
            if (!IsRunFile(Path.GetFileName(file)) || now - File.GetLastWriteTime(file) <= TimeSpan.FromDays(retentionDays))
                continue;
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // try again tomorrow
            }
        }
        return deleted;
    }
}

/// <summary>Applies the run file retention at startup and once a day.</summary>
public sealed class RetentionService(WebSettings settings, ILogger<RetentionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            int deleted = RunFiles.Prune(settings.DataDir, settings.RetentionDays, DateTime.Now);
            if (deleted > 0)
                log.LogInformation("Deleted {Count} run files older than {Days} days", deleted, settings.RetentionDays);
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
