using System.Globalization;
using System.Text;
using MotorBench.Plc;

namespace MotorBench.Logging;

public sealed record AlarmEvent(DateTime Time, AlarmCode Code, bool Raised)
{
    public string Text => $"{(Raised ? "発生" : "解除")}: {BenchText.Describe(Code)}";
}

/// <summary>Turns the alarm register into raised/cleared events and keeps a history file.</summary>
public sealed class AlarmTracker
{
    private readonly string? _filePath;
    private AlarmCode _last = AlarmCode.None;

    public AlarmTracker(string? filePath)
    {
        _filePath = filePath;
    }

    public event Action<AlarmEvent>? Changed;

    public void Update(BenchStatus status)
    {
        if (status.Alarm == _last)
            return;

        if (_last != AlarmCode.None)
            Emit(new AlarmEvent(status.Timestamp, _last, Raised: false));
        if (status.Alarm != AlarmCode.None)
            Emit(new AlarmEvent(status.Timestamp, status.Alarm, Raised: true));

        _last = status.Alarm;
    }

    /// <summary>Forget the last state, e.g. after reconnecting.</summary>
    public void Clear() => _last = AlarmCode.None;

    private void Emit(AlarmEvent e)
    {
        Changed?.Invoke(e);
        if (_filePath is null)
            return;

        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            bool newFile = !File.Exists(_filePath);
            using var w = new StreamWriter(_filePath, append: true, new UTF8Encoding(true));
            if (newFile)
                w.WriteLine("time,code,event,text");
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{e.Time:yyyy-MM-dd HH:mm:ss.fff},{(int)e.Code},{(e.Raised ? "raised" : "cleared")},{BenchText.Describe(e.Code)}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // history file is best effort, the event is still shown in the HMI
        }
    }
}
