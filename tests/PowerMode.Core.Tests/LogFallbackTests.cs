using PowerMode.Core.Logging;

namespace PowerMode.Core.Tests;

public sealed class LogFallbackTests
{
    [Fact]
    public void A_daily_log_that_cannot_be_written_falls_back_and_reports_once()
    {
        using var dir = new TempDir();

        // A file where the log folder should be: creating the folder fails.
        var blocked = dir.File("logs");
        File.WriteAllText(blocked, "not a folder");
        var log = new FileLog(blocked, new ManualTime(new DateTimeOffset(2026, 11, 9, 10, 27, 52, TimeSpan.FromHours(2))), dir.File("fallback"));
        var reports = new List<string>();
        log.Failed += (_, reason) => reports.Add(reason);

        log.Write("tray", "Starting (pid 20860)");
        log.Write("instance", "Forwarded: --open");

        var fallback = File.ReadAllText(log.FallbackFile);
        Assert.Contains("[tray] Starting (pid 20860)", fallback, StringComparison.Ordinal);
        Assert.Contains("[instance] Forwarded: --open", fallback, StringComparison.Ordinal);
        Assert.Contains("daily log failed: IOException", fallback, StringComparison.Ordinal);
        Assert.Single(reports);
        Assert.Contains(blocked, reports[0], StringComparison.Ordinal);
        Assert.EndsWith(FileLog.FallbackFileName, log.FallbackFile, StringComparison.Ordinal);
    }

    [Fact]
    public void A_working_log_never_touches_the_fallback()
    {
        using var dir = new TempDir();
        var log = new FileLog(dir.File("logs"), new ManualTime(DateTimeOffset.UnixEpoch), dir.File("fallback"));
        var failed = false;
        log.Failed += (_, _) => failed = true;

        log.Write("tray", "Started");

        Assert.False(failed);
        Assert.False(File.Exists(log.FallbackFile));
        Assert.Contains("[tray] Started", File.ReadAllText(log.CurrentFile), StringComparison.Ordinal);
        Assert.Contains(Path.Combine(Path.GetTempPath(), "PowerMode"), new FileLog(dir.Path, TimeProvider.System).FallbackFile, StringComparison.Ordinal);
    }
}

public sealed class LogSharingTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 9, 10, 27, 52, TimeSpan.FromHours(2));

    [Fact]
    public void A_file_held_by_another_writer_for_a_moment_is_retried_without_a_false_alarm()
    {
        using var dir = new TempDir();
        var folder = dir.File("logs");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileLog.FileNameFor(Day));
        var holder = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var waits = new List<TimeSpan>();
        var log = new FileLog(folder, new ManualTime(Day), dir.File("fallback"), wait =>
        {
            waits.Add(wait);
            holder.Dispose();
        });
        var failed = false;
        log.Failed += (_, _) => failed = true;

        log.Write("cli", "Status");

        Assert.False(failed);
        Assert.Equal([TimeSpan.FromMilliseconds(10)], waits);
        Assert.False(File.Exists(log.FallbackFile));
        Assert.Contains("[cli] Status", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_held_past_the_retries_still_falls_back_and_reports()
    {
        using var dir = new TempDir();
        var folder = dir.File("logs");
        Directory.CreateDirectory(folder);
        using var holder = new FileStream(Path.Combine(folder, FileLog.FileNameFor(Day)), FileMode.Append, FileAccess.Write, FileShare.Read);
        var waits = 0;
        var log = new FileLog(folder, new ManualTime(Day), dir.File("fallback"), _ => waits++);
        var reports = new List<string>();
        log.Failed += (_, reason) => reports.Add(reason);

        log.Write("cli", "Status");

        Assert.Equal(FileLog.WriteAttempts - 1, waits);
        Assert.Single(reports);
        Assert.Contains("[cli] Status", File.ReadAllText(log.FallbackFile), StringComparison.Ordinal);
    }

    [Fact]
    public void The_fallback_file_is_pruned_when_old_or_large_and_kept_otherwise()
    {
        using var dir = new TempDir();
        var log = new FileLog(dir.File("missing"), new ManualTime(Day), dir.File("fallback"));
        Directory.CreateDirectory(dir.File("fallback"));

        File.WriteAllText(log.FallbackFile, "recent");
        File.SetLastWriteTime(log.FallbackFile, Day.LocalDateTime);
        Assert.Equal(0, log.Prune());
        Assert.True(File.Exists(log.FallbackFile));

        File.SetLastWriteTime(log.FallbackFile, Day.LocalDateTime.AddDays(-FileLog.RetentionDays - 1));
        Assert.Equal(1, log.Prune());
        Assert.False(File.Exists(log.FallbackFile));

        using (var large = new FileStream(log.FallbackFile, FileMode.Create))
        {
            large.SetLength(FileLog.MaxFallbackBytes + 1);
        }

        File.SetLastWriteTime(log.FallbackFile, Day.LocalDateTime);
        Assert.Equal(1, log.Prune());
        Assert.False(File.Exists(log.FallbackFile));
    }
}
