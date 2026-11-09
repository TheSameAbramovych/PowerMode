using System.Globalization;

namespace PowerMode.Core.Logging;

public interface ILog
{
    void Write(string category, string message);
}

public static class LogExtensions
{
    public static void Error(this ILog log, string category, string message, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(exception);
        log.Write(category, $"{message}: {exception.GetType().Name}: {exception.Message}");
    }
}

/// <summary>Plain-text daily log <c>logs\powermode-YYYYMMDD.log</c>; files older than <see cref="RetentionDays"/> are deleted.</summary>
public sealed class FileLog : ILog
{
    public const int RetentionDays = 14;
    private const string FilePrefix = "powermode-";
    private const string FileExtension = ".log";
    private const string DateFormat = "yyyyMMdd";

    public const string FallbackFileName = "powermode-fallback.log";

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly string _fallbackDirectory;
    private readonly Lock _gate = new();
    private bool _failureReported;

    /// <param name="directory">The daily log folder.</param>
    /// <param name="time">The clock.</param>
    /// <param name="fallbackDirectory">Where lines go when the daily file cannot be written; <c>%TEMP%\PowerMode</c> by default.</param>
    public FileLog(string directory, TimeProvider time, string? fallbackDirectory = null)
    {
        _directory = directory;
        _time = time;
        _fallbackDirectory = fallbackDirectory ?? Path.Combine(Path.GetTempPath(), "PowerMode");
    }

    /// <summary>
    /// Raised once, with the reason, the first time a line cannot go to the daily file (it went to the fallback file
    /// instead), so a failing log is never silent. Raised on the writing thread, outside the log's lock.
    /// </summary>
    public event EventHandler<string>? Failed;

    public string FallbackFile => Path.Combine(_fallbackDirectory, FallbackFileName);

    public string CurrentFile => Path.Combine(_directory, FileNameFor(_time.GetLocalNow()));

    public static string FileNameFor(DateTimeOffset day) =>
        FilePrefix + day.ToString(DateFormat, CultureInfo.InvariantCulture) + FileExtension;

    /// <summary>
    /// Append a line to the daily file. Never throws (logging must never break a switch), and never fails silently:
    /// a line the daily file does not take goes to <see cref="FallbackFile"/> with the reason, and <see cref="Failed"/>
    /// is raised the first time.
    /// </summary>
    public void Write(string category, string message)
    {
        string? failure = null;
        lock (_gate)
        {
            var line = $"[{category}] {message}";
            try
            {
                var now = _time.GetLocalNow();
                line = string.Create(CultureInfo.InvariantCulture, $"{now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
                Directory.CreateDirectory(_directory);
                File.AppendAllText(Path.Combine(_directory, FileNameFor(now)), line);
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var reason = $"{ex.GetType().Name}: {ex.Message}";
                WriteFallback(line, reason);
                if (!_failureReported)
                {
                    _failureReported = true;
                    failure = $"PowerMode cannot write its log in {_directory} ({reason}); lines go to {FallbackFile}.";
                }
            }
        }

        if (failure is not null)
        {
            Failed?.Invoke(this, failure);
        }
    }

    private void WriteFallback(string line, string reason)
    {
        try
        {
            Directory.CreateDirectory(_fallbackDirectory);
            var stamped = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} pid {Environment.ProcessId} {line.TrimEnd()} (daily log failed: {reason}){Environment.NewLine}");
            File.AppendAllText(FallbackFile, stamped);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere left to write; the Failed event still reports it.
        }
    }

    /// <summary>Delete log files older than the retention window. Returns how many were deleted.</summary>
    public int Prune()
    {
        if (!Directory.Exists(_directory))
        {
            return 0;
        }

        var cutoff = _time.GetLocalNow().Date.AddDays(-RetentionDays);
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(_directory, FilePrefix + "*" + FileExtension))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];
            if (DateTime.TryParseExact(stamp, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < cutoff)
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (IOException)
                {
                    // Locked by a viewer; try again next start.
                }
            }
        }

        return deleted;
    }
}
