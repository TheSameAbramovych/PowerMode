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

    /// <summary>A fallback file larger than this is deleted by <see cref="Prune"/> (it is also deleted after <see cref="RetentionDays"/>).</summary>
    public const long MaxFallbackBytes = 5 * 1024 * 1024;

    /// <summary>Another PowerMode process (tray, CLI) appending at the same moment: retry this often before falling back.</summary>
    public const int WriteAttempts = 5;
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly string _fallbackDirectory;
    private readonly Action<TimeSpan> _retryWait;
    private readonly Lock _gate = new();
    private bool _failureReported;

    /// <param name="directory">The daily log folder.</param>
    /// <param name="time">The clock.</param>
    /// <param name="fallbackDirectory">Where lines go when the daily file cannot be written; <c>%TEMP%\PowerMode</c> by default.</param>
    /// <param name="retryWait">How a write waits before it retries a file another process holds; <see cref="Thread.Sleep(TimeSpan)"/> by default.</param>
    public FileLog(string directory, TimeProvider time, string? fallbackDirectory = null, Action<TimeSpan>? retryWait = null)
    {
        _retryWait = retryWait ?? Thread.Sleep;
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
                Append(Path.Combine(_directory, FileNameFor(now)), line);
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

    /// <summary>
    /// Append with the default exclusive-writer sharing; while another process writes the same file at that moment
    /// (a sharing or lock violation), wait 10, 20, 30, 40 ms and try again. Anything else, or still held, throws.
    /// </summary>
    private void Append(string path, string line)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.AppendAllText(path, line);
                return;
            }
            catch (IOException ex) when (attempt < WriteAttempts && ex.HResult is SharingViolation or LockViolation)
            {
                _retryWait(TimeSpan.FromMilliseconds(10 * attempt));
            }
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

    /// <summary>
    /// Delete log files older than the retention window, and the fallback file once it is that old or larger than
    /// <see cref="MaxFallbackBytes"/>. Returns how many were deleted.
    /// </summary>
    public int Prune()
    {
        var cutoff = _time.GetLocalNow().Date.AddDays(-RetentionDays);
        var deleted = PruneFallback(cutoff) ? 1 : 0;
        if (!Directory.Exists(_directory))
        {
            return deleted;
        }

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

    private bool PruneFallback(DateTime cutoff)
    {
        var file = new FileInfo(FallbackFile);
        try
        {
            if (file.Exists && (file.Length > MaxFallbackBytes || file.LastWriteTime < cutoff))
            {
                file.Delete();
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held by another PowerMode process; try again next start.
        }

        return false;
    }
}
