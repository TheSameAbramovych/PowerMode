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
