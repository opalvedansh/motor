using System.Text;

namespace MotorBench.Web;

/// <summary>
/// Append-only data/audit.csv: who did what, when, and what the server answered.
/// UTF-8 with BOM like the other CSV files, so Excel opens it correctly.
/// </summary>
public sealed class AuditLog(WebSettings settings, ILogger<AuditLog> log)
{
    private readonly object _sync = new();

    public string FilePath => Path.Combine(settings.DataDir, "audit.csv");

    public void Write(string user, string action, string detail, int result)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff},{Field(user)},{Field(action)},{Field(detail)},{result}";
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(settings.DataDir);
                bool newFile = !File.Exists(FilePath);
                using var w = new StreamWriter(FilePath, append: true, new UTF8Encoding(true));
                if (newFile)
                    w.WriteLine("time,user,action,detail,result");
                w.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // shortcut: a failed audit write does not block the command; make it blocking if audits become a compliance requirement
                log.LogError(ex, "Could not write audit entry: {Line}", line);
            }
        }
    }

    /// <summary>CSV-quote, and defuse values Excel would run as a formula.</summary>
    internal static string Field(string value)
    {
        value = value.ReplaceLineEndings(" ");
        if (value.Length > 0 && "=+-@".Contains(value[0]))
            value = "'" + value;
        return value.IndexOfAny([',', '"']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
