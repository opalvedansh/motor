using System.Globalization;
using MotorBench.Plc;

namespace MotorBench.Sequencing;

public sealed record TestStep(int Number, int SpeedRpm, int LoadPct, double DwellSeconds);

/// <summary>
/// A list of test steps loaded from CSV:
/// <code>
/// # comment
/// speed_rpm,load_pct,dwell_s
/// 500,0,5
/// 1500,50,10
/// </code>
/// </summary>
public sealed class TestProfile
{
    public TestProfile(string name, IReadOnlyList<TestStep> steps)
    {
        Name = name;
        Steps = steps;
    }

    public string Name { get; }
    public IReadOnlyList<TestStep> Steps { get; }

    public double TotalDwellSeconds => Steps.Sum(s => s.DwellSeconds);

    public static TestProfile Load(string path) =>
        Parse(Path.GetFileNameWithoutExtension(path), File.ReadLines(path));

    public static TestProfile Parse(string name, IEnumerable<string> lines)
    {
        var steps = new List<TestStep>();
        int lineNo = 0;

        foreach (var raw in lines)
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (steps.Count == 0 && char.IsLetter(line[0]))
                continue; // header row

            var cols = line.Split(',');
            if (cols.Length != 3)
                throw new ProfileFormatException(lineNo, "expected 3 columns: speed_rpm,load_pct,dwell_s");

            int speed = ParseInt(cols[0], lineNo, "speed_rpm");
            int load = ParseInt(cols[1], lineNo, "load_pct");
            double dwell = ParseDouble(cols[2], lineNo, "dwell_s");

            if (speed < 0 || speed > RegisterMap.MaxSpeedRpm)
                throw new ProfileFormatException(lineNo, $"speed_rpm must be 0..{RegisterMap.MaxSpeedRpm}");
            if (load < 0 || load > RegisterMap.MaxLoadPct)
                throw new ProfileFormatException(lineNo, $"load_pct must be 0..{RegisterMap.MaxLoadPct}");
            if (!(dwell > 0 && dwell <= 3600)) // also rejects NaN
                throw new ProfileFormatException(lineNo, "dwell_s must be > 0 and <= 3600");

            steps.Add(new TestStep(steps.Count + 1, speed, load, dwell));
        }

        if (steps.Count == 0)
            throw new ProfileFormatException(0, "profile has no steps");

        return new TestProfile(name, steps);
    }

    private static int ParseInt(string s, int line, string column)
    {
        if (!int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            throw new ProfileFormatException(line, $"{column} is not a whole number: '{s.Trim()}'");
        return value;
    }

    private static double ParseDouble(string s, int line, string column)
    {
        if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            throw new ProfileFormatException(line, $"{column} is not a number: '{s.Trim()}'");
        return value;
    }
}

public sealed class ProfileFormatException : Exception
{
    public ProfileFormatException(int line, string message)
        : base(line > 0 ? $"line {line}: {message}" : message)
    {
        Line = line;
    }

    public int Line { get; }
}
