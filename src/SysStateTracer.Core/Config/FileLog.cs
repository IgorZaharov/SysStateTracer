using System.Globalization;
using System.Text;

namespace SysStateTracer.Core.Config;

/// <summary>
/// Append-only text log (SysStateTracer.log). When the file passes <see cref="MaxBytes"/> it is
/// renamed to <c>SysStateTracer.log.1</c> (replacing the previous one) and a new file starts.
/// Logging never throws: a log that cannot be written is silently skipped.
/// </summary>
public sealed class FileLog
{
    public const long DefaultMaxBytes = 5 * 1024 * 1024;

    private readonly Lock _gate = new();

    public FileLog(string path, long maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1024);
        Path = path;
        MaxBytes = maxBytes;
    }

    public string Path { get; }

    public long MaxBytes { get; }

    /// <summary>Path of the rolled-over previous log.</summary>
    public string PreviousPath => Path + ".1";

    public void Write(string level, string message, Exception? exception = null)
    {
        var text = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ")
            .Append(message);

        if (exception is not null)
        {
            text.AppendLine().Append(exception);
        }

        text.AppendLine();

        lock (_gate)
        {
            try
            {
                RollIfNeeded();
                File.AppendAllText(Path, text.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never break the app.
            }
        }
    }

    private void RollIfNeeded()
    {
        var info = new FileInfo(Path);
        if (info.Exists && info.Length >= MaxBytes)
        {
            File.Move(Path, PreviousPath, overwrite: true);
        }
    }
}

/// <summary>Process-wide log. Does nothing until <see cref="Current"/> is set at startup.</summary>
public static class Log
{
    public static FileLog? Current { get; set; }

    public static void Info(string message) => Current?.Write("INF", message);

    public static void Warning(string message, Exception? exception = null) => Current?.Write("WRN", message, exception);

    public static void Error(string message, Exception? exception = null) => Current?.Write("ERR", message, exception);
}
