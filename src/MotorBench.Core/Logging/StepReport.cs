using System.Globalization;
using System.Text;
using MotorBench.Sequencing;

namespace MotorBench.Logging;

public static class StepReport
{
    /// <summary>
    /// Writes the step results of one sequence run. Returns the file path.
    /// <paramref name="info"/> adds <c># key,value</c> header lines (operator, DUT serial, ...).
    /// </summary>
    public static string Write(string directory, TestProfile profile, IReadOnlyList<StepResult> results,
        string outcome, DateTime finished, IEnumerable<KeyValuePair<string, string>>? info = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"result_{profile.Name}_{finished:yyyyMMdd_HHmmss}.csv");
        var inv = CultureInfo.InvariantCulture;

        using var w = new StreamWriter(path, append: false, new UTF8Encoding(true));
        w.WriteLine($"# profile,{profile.Name}");
        w.WriteLine(string.Create(inv, $"# finished,{finished:yyyy-MM-dd HH:mm:ss}"));
        w.WriteLine($"# outcome,{outcome}");
        foreach (var (key, value) in info ?? [])
            w.WriteLine($"# {key},{value.ReplaceLineEndings(" ")}");
        w.WriteLine("step,speed_sp_rpm,load_pct,dwell_s,settle_s,avg_speed_rpm,avg_torque_nm,max_temp_c");

        foreach (var r in results)
        {
            w.WriteLine(string.Create(inv,
                $"{r.Step.Number},{r.Step.SpeedRpm},{r.Step.LoadPct},{r.Step.DwellSeconds:0.##},{r.SettleSeconds:0.00}," +
                $"{r.AvgSpeedRpm:0.0},{r.AvgTorqueNm:0.00},{r.MaxTempC:0.0}"));
        }

        return path;
    }
}
