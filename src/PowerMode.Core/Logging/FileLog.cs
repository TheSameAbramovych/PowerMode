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

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    public FileLog(string directory, TimeProvider time)
    {
        _directory = directory;
        _time = time;
    }

    public string CurrentFile => Path.Combine(_directory, FileNameFor(_time.GetLocalNow()));

    public static string FileNameFor(DateTimeOffset day) =>
        FilePrefix + day.ToString(DateFormat, CultureInfo.InvariantCulture) + FileExtension;

    public void Write(string category, string message)
    {
        var now = _time.GetLocalNow();
        var line = string.Create(CultureInfo.InvariantCulture, $"{now:yyyy-MM-dd HH:mm:ss.fff} [{category}] {message}{Environment.NewLine}");
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(Path.Combine(_directory, FileNameFor(now)), line);
            }
            catch (IOException)
            {
                // Logging must never break a switch.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
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
