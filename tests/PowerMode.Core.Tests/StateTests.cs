using PowerMode.Core.Levers;
using PowerMode.Core.Model;
using PowerMode.Core.Presentation;
using PowerMode.Core.State;

namespace PowerMode.Core.Tests;

public sealed class StateTests
{
    [Fact]
    public void Snapshot_and_state_round_trip_through_json()
    {
        var state = new PersistedState
        {
            Mode = PowerModeKind.Reserve,
            Since = new DateTimeOffset(2026, 11, 9, 14, 32, 0, TimeSpan.FromHours(2)),
            LastApplied = DateTimeOffset.UnixEpoch,
            Snapshot = RenderFixtures.Baseline(),
            EfficiencyApplied = [new EfficiencyRecord(42, "Slack", DateTimeOffset.UnixEpoch, 32)],
            ClosedApps = ["steam"],
            LastResults = { [LeverKind.Gpu] = LeverResult.Failed(LeverResult.NeedsAdministrator, needsElevation: true) with { Duration = TimeSpan.FromMilliseconds(12) } },
        };
        state.OptionsFor(PowerModeKind.Station).KeepDocker = true;

        var json = StateStore.Serialize(state);
        var copy = StateStore.Deserialize(json)!;

        Assert.Equal(json, StateStore.Serialize(copy));
        Assert.Equal(PowerModeKind.Reserve, copy.Mode);
        Assert.Contains("\"mode\": \"reserve\"", json, StringComparison.Ordinal);
        var snapshot = copy.Snapshot!;
        Assert.Equal(Guid.Parse("9897998c-92de-4669-853f-b7cd3ecb2790"), snapshot.ActiveScheme);
        Assert.Equal(new MonitorLevel(@"\\.\DISPLAY1#0", 80, 50), snapshot.Monitors.Single());
        Assert.Equal(new DisplayModeInfo(@"\\.\DISPLAY1", 2560, 1440, 199, 32), snapshot.Displays.Single());
        Assert.Equal(new GpuSnapshot(120_000, 120_000), snapshot.Gpu);
        Assert.True(snapshot.WallpaperEngineRunning);
        Assert.Equal(6, snapshot.Docker!.RunningContainers.Count);
        Assert.Equal("EPSDNMON", snapshot.Processes.Single().Name);
        Assert.True(snapshot.Services["WSearch"]);
        Assert.True(copy.OptionsFor(PowerModeKind.Station).KeepDocker);
        Assert.Equal(42, copy.EfficiencyApplied.Single().ProcessId);
        Assert.True(copy.LastResults[LeverKind.Gpu].NeedsElevation);
        Assert.Equal(TimeSpan.FromMilliseconds(12), copy.LastResults[LeverKind.Gpu].Duration);
    }

    [Fact]
    public void Store_saves_atomically_and_loads()
    {
        using var dir = new TempDir();
        var store = new StateStore(Path.Combine(dir.Path, "nested", "state.json"), new ListLog());

        store.Save(new PersistedState { Mode = PowerModeKind.Station });

        Assert.Equal(PowerModeKind.Station, store.Load().Mode);
        Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public void Missing_or_corrupt_state_starts_from_grid()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        var store = new StateStore(dir.File("state.json"), log);

        Assert.Equal(PowerModeKind.Grid, store.Load().Mode);

        File.WriteAllText(store.Path, "{ corrupt");
        var loaded = store.Load();

        Assert.Equal(PowerModeKind.Grid, loaded.Mode);
        Assert.Null(loaded.Snapshot);
        Assert.Contains(log.Lines, l => l.Contains("starting from Grid", StringComparison.Ordinal));

        File.WriteAllText(store.Path, "null");
        Assert.Equal(PowerModeKind.Grid, store.Load().Mode);
    }

    [Fact]
    public void Save_failures_are_logged_not_thrown()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        var store = new StateStore(dir.File("state.json"), log);
        Directory.CreateDirectory(store.Path + ".tmp");

        store.Save(new PersistedState());

        Assert.Contains(log.Lines, l => l.Contains("Cannot write", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(LeverKind.PowerScheme)]
    [InlineData(LeverKind.DisplayMode)]
    [InlineData(LeverKind.Brightness)]
    [InlineData(LeverKind.Gpu)]
    [InlineData(LeverKind.WallpaperEngine)]
    [InlineData(LeverKind.Docker)]
    [InlineData(LeverKind.Apps)]
    [InlineData(LeverKind.Services)]
    [InlineData(LeverKind.Efficiency)]
    public void Snapshot_parts_can_be_copied_from_an_older_baseline(LeverKind kind)
    {
        var older = RenderFixtures.Baseline();
        var fresh = new Snapshot();

        fresh.CopyPartFrom(older, kind);

        var expectedCopied = kind switch
        {
            LeverKind.PowerScheme => fresh.ActiveScheme == older.ActiveScheme,
            LeverKind.DisplayMode => fresh.Displays.SequenceEqual(older.Displays),
            LeverKind.Brightness => fresh.Monitors.SequenceEqual(older.Monitors),
            LeverKind.Gpu => fresh.Gpu == older.Gpu,
            LeverKind.WallpaperEngine => fresh.WallpaperEngineRunning,
            LeverKind.Docker => fresh.Docker == older.Docker,
            LeverKind.Apps => fresh.Processes.SequenceEqual(older.Processes),
            LeverKind.Services => fresh.Services.Count == 7,
            _ => fresh.Gpu is null,
        };
        Assert.True(expectedCopied);
    }

    [Fact]
    public void Copy_part_rejects_unknown_levers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Snapshot().CopyPartFrom(new Snapshot(), (LeverKind)99));
    }
}
