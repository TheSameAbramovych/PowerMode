using PowerMode.Core.Levers;
using PowerMode.Core.Logging;
using PowerMode.Core.Model;
using PowerMode.Core.Presentation;

namespace PowerMode.Core.Tests;

public sealed class NamesTests
{
    [Fact]
    public void Mode_names_round_trip()
    {
        foreach (var mode in ModeNames.All)
        {
            Assert.True(ModeNames.TryParse(ModeNames.Key(mode), out var parsed));
            Assert.Equal(mode, parsed);
            Assert.Equal(ModeNames.Key(mode), ModeNames.Display(mode).ToLowerInvariant());
        }

        Assert.False(ModeNames.TryParse("turbo", out _));
        Assert.False(ModeNames.TryParse(null, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModeNames.Display((PowerModeKind)7));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModeNames.Key((PowerModeKind)7));
    }

    [Fact]
    public void Every_row_has_a_title_and_levers_and_every_lever_has_names()
    {
        Assert.Equal(8, LeverRows.Ordered.Count);
        foreach (var row in LeverRows.Ordered)
        {
            Assert.NotEmpty(LeverRows.Title(row));
            Assert.NotEmpty(LeverRows.LeversOf(row));
        }

        foreach (var kind in Enum.GetValues<LeverKind>())
        {
            Assert.NotEmpty(LeverRows.ElevationNoun(kind));
            Assert.NotEmpty(FriendlyNames.Lever(kind));
            Assert.Contains(LeverRows.Ordered, r => LeverRows.LeversOf(r).Contains(kind));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => LeverRows.Title((LeverRow)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => LeverRows.LeversOf((LeverRow)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => LeverRows.ElevationNoun((LeverKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => FriendlyNames.Lever((LeverKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => FlyoutStateBuilder.Describe((LeverRow)99, new TargetState { Mode = PowerModeKind.Grid }));
        Assert.Throws<ArgumentOutOfRangeException>(() => FlyoutStateBuilder.Describe((LeverRow)99, new TargetState { Mode = PowerModeKind.Station }));
    }

    [Fact]
    public void Friendly_names_fall_back_to_the_raw_name()
    {
        Assert.Equal("Steam", FriendlyNames.Process("STEAM"));
        Assert.Equal("Viber", FriendlyNames.Process("Viber"));
        Assert.Equal("Search", FriendlyNames.Service("wsearch"));
        Assert.Equal("MongoDB", FriendlyNames.Service("MongoDB"));
    }

    [Fact]
    public void Power_settings_use_the_guids_measured_on_this_pc()
    {
        Assert.Equal(Guid.Parse("be337238-0d82-4146-a960-4f3749d470c7"), PowerSettings.BoostMode.Setting);
        Assert.Equal(PowerSettings.SubProcessor, PowerSettings.MaxProcessor.Subgroup);
        Assert.Equal(PowerSettings.SubProcessor, PowerSettings.MinProcessor.Subgroup);
        Assert.Equal(PowerSettings.SubVideo, PowerSettings.DisplayOff.Subgroup);
        Assert.Equal(Guid.Parse("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"), PowerSettings.Sleep.Setting);
        Assert.Equal(Guid.Parse("9d7815a6-7ee4-497e-8888-515a05f02364"), PowerSettings.Hibernate.Setting);
        Assert.Equal("HIBERNATEIDLE", PowerSettings.Hibernate.Name);
        Assert.Equal(0u, PowerSettings.BoostDisabled);
        Assert.Equal(2u, PowerSettings.BoostAggressive);
    }

    [Fact]
    public void Levers_have_no_predecessor_by_default()
    {
        ILever lever = new BareLever();

        Assert.Null(lever.RunsAfter);
    }

    private sealed class BareLever : ILever
    {
        public LeverKind Kind => LeverKind.Gpu;

        public Task CaptureAsync(Snapshot snapshot, Config.AppConfig config, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<LeverPlan> PlanAsync(ApplyContext context, CancellationToken cancellationToken) => Task.FromResult(LeverPlan.None);
    }

    [Fact]
    public void Own_schemes_are_recognised()
    {
        Assert.True(PowerSchemes.IsOwn(PowerSchemes.Station));
        Assert.True(PowerSchemes.IsOwn(PowerSchemes.Reserve));
        Assert.False(PowerSchemes.IsOwn(PowerSchemes.Balanced));
    }
}

public sealed class FileLogTests
{
    [Fact]
    public void Writes_daily_files_and_prunes_old_ones()
    {
        using var dir = new TempDir();
        var time = new ManualTime(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));
        var log = new FileLog(Path.Combine(dir.Path, "logs"), time, dir.File("fallback"));

        log.Write("lever", "Gpu: Ok - Already set (12 ms)");
        log.Error("lever", "boom", new InvalidOperationException("bad"));

        var text = File.ReadAllText(log.CurrentFile);
        Assert.EndsWith("powermode-20261109.log", log.CurrentFile, StringComparison.Ordinal);
        Assert.Contains("[lever] Gpu: Ok - Already set (12 ms)", text, StringComparison.Ordinal);
        Assert.Contains("boom: InvalidOperationException: bad", text, StringComparison.Ordinal);

        var logs = Path.Combine(dir.Path, "logs");
        File.WriteAllText(Path.Combine(logs, "powermode-20261020.log"), "old");
        File.WriteAllText(Path.Combine(logs, "powermode-20261026.log"), "kept");
        File.WriteAllText(Path.Combine(logs, "powermode-notadate.log"), "ignored");

        Assert.Equal(1, log.Prune());
        Assert.False(File.Exists(Path.Combine(logs, "powermode-20261020.log")));
        Assert.True(File.Exists(Path.Combine(logs, "powermode-20261026.log")));
        Assert.True(File.Exists(Path.Combine(logs, "powermode-notadate.log")));
    }

    [Fact]
    public void Prune_without_directory_does_nothing_and_write_failures_are_swallowed()
    {
        using var dir = new TempDir();
        var blocker = dir.File("file-not-dir");
        File.WriteAllText(blocker, "x");
        var log = new FileLog(blocker, TimeProvider.System, dir.File("fallback"));

        Assert.Equal(0, new FileLog(dir.File("missing"), TimeProvider.System, dir.File("fallback")).Prune());
        log.Write("x", "cannot be written under a file");
    }
}
