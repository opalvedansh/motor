using System.Globalization;
using System.Text;
using MotorBench.Plc;

namespace MotorBench.Logging;

/// <summary>One CSV row per status poll. UTF-8 with BOM so Excel opens it correctly.</summary>
public sealed class CsvDataLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly DateTime _start;

    public CsvDataLogger(string directory, string prefix, DateTime start)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"{prefix}_{start:yyyyMMdd_HHmmss}.csv");
        _start = start;
        _writer = new StreamWriter(FilePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
        {
            AutoFlush = true,
        };
        _writer.WriteLine("time,elapsed_s,state,speed_sp_rpm,speed_rpm,load_pct,torque_nm,temp_c,alarm");
    }

    public string FilePath { get; }

    public void Write(BenchStatus s)
    {
        _writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{s.Timestamp:yyyy-MM-dd HH:mm:ss.fff},{(s.Timestamp - _start).TotalSeconds:0.000},{s.State}," +
            $"{s.SpeedSetpointRpm},{s.SpeedRpm},{s.LoadSetpointPct},{s.TorqueNm:0.0},{s.MotorTempC:0.0},{(int)s.Alarm}"));
    }

    public void Dispose() => _writer.Dispose();
}
