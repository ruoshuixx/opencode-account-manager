using System.Text;

namespace AccountManager.Core.Logging;

public enum LogLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>
/// Writes redacted run/error logs to a logs directory and echoes every entry to the UI.
/// Equivalent to <c>Initialize-Logging</c> / <c>Write-LocalLog</c> / <c>Write-LocalError</c>.
/// </summary>
public sealed class AppLog
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Security.SecretRegistry _secrets;

    public AppLog(Security.SecretRegistry secrets, string directory)
    {
        _secrets = secrets;
        _directory = directory;
        Directory.CreateDirectory(directory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..6];
        RunLogPath = Path.Combine(directory, "run-" + stamp + ".log");
        ErrorLogPath = Path.Combine(directory, "errors-" + stamp + ".log");
        File.WriteAllText(RunLogPath, string.Empty, new UTF8Encoding(false));
        File.WriteAllText(ErrorLogPath, string.Empty, new UTF8Encoding(false));
    }

    public string RunLogPath { get; }

    public string ErrorLogPath { get; }

    public event EventHandler<LogEntry>? EntryWritten;

    public void Info(string message) => Write(message, LogLevel.Info);

    public void Warn(string message) => Write(message, LogLevel.Warn);

    public void Error(string message, Exception? ex = null)
    {
        var line = ex is null ? message : message + "：" + ex.Message;
        Write(line, LogLevel.Error);
    }

    private void Write(string message, LogLevel level)
    {
        var safe = _secrets.Redact(message);
        var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level.ToString().ToUpperInvariant() + "] " + safe + Environment.NewLine;
        lock (_gate)
        {
            try
            {
                File.AppendAllText(RunLogPath, line, new UTF8Encoding(false));
                if (level == LogLevel.Error) { File.AppendAllText(ErrorLogPath, line, new UTF8Encoding(false)); }
            }
            catch
            {
                // Disk logging is best-effort; never let it break an operation.
            }
        }

        EntryWritten?.Invoke(this, new LogEntry(DateTime.Now, level, safe));
    }
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message);
