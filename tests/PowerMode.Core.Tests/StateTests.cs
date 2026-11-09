using System.Text.Json;
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
        state.WithOptions(PowerModeKind.Station, new ModeOptions { KeepDocker = true });

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
    public void Missing_or_corrupt_state_without_a_backup_starts_from_grid_and_keeps_the_bad_file()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        var store = new StateStore(dir.File("state.json"), log);

        Assert.Equal(PowerModeKind.Grid, store.Load().Mode);

        File.WriteAllText(store.Path, "{ corrupt");
        var loaded = store.Load();

        Assert.Equal(PowerModeKind.Grid, loaded.Mode);
        Assert.Null(loaded.Snapshot);
        Assert.Contains(log.Lines, l => l.Contains("the baseline is lost", StringComparison.Ordinal));
        Assert.False(File.Exists(store.Path));
        var kept = Assert.Single(Directory.GetFiles(dir.Path, "state.json.corrupt-*"));
        Assert.Equal("{ corrupt", File.ReadAllText(kept));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{\"mode\":\"station\",\"snapshot\":{\"monit")]
    [InlineData("\0\0\0\0\0\0\0\0")]
    public void An_empty_truncated_or_zeroed_state_recovers_from_the_backup(string bad)
    {
        using var dir = new TempDir();
        var log = new ListLog();
        var store = new StateStore(dir.File("state.json"), log);
        store.Save(new PersistedState { Mode = PowerModeKind.Station, Snapshot = RenderFixtures.Baseline() });
        store.Save(new PersistedState { Mode = PowerModeKind.Reserve, Snapshot = RenderFixtures.Baseline() });
        Assert.Equal(PowerModeKind.Station, StateStore.Deserialize(File.ReadAllText(store.BackupPath))!.Mode);

        File.WriteAllText(store.Path, bad);
        var loaded = store.Load();

        Assert.Equal(PowerModeKind.Station, loaded.Mode);
        Assert.Equal(80, loaded.Snapshot!.Monitors.Single().Brightness);
        Assert.Single(Directory.GetFiles(dir.Path, "state.json.corrupt-*"));
        Assert.Contains(log.Lines, l => l.Contains("Recovered the state", StringComparison.Ordinal));
    }

    [Fact]
    public void A_corrupt_backup_too_starts_from_grid()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        var store = new StateStore(dir.File("state.json"), log);
        File.WriteAllText(store.Path, "{");
        File.WriteAllText(store.BackupPath, "{");

        Assert.Equal(PowerModeKind.Grid, store.Load().Mode);
        Assert.True(File.Exists(store.BackupPath));
        Assert.Contains(log.Lines, l => l.Contains("the baseline is lost", StringComparison.Ordinal));
    }

    [Fact]
    public void A_locked_state_file_is_not_set_aside()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        var store = new StateStore(dir.File("state.json"), log);
        store.Save(new PersistedState { Mode = PowerModeKind.Station });

        using (new FileStream(store.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(PowerModeKind.Grid, store.Load().Mode);
        }

        Assert.True(File.Exists(store.Path));
        Assert.Empty(Directory.GetFiles(dir.Path, "state.json.corrupt-*"));
        Assert.Contains(log.Lines, l => l.Contains("Cannot read", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_enum_values_from_another_version_keep_the_snapshot()
    {
        var json = """
            {
              "mode": "station",
              "snapshot": { "monitors": [ { "id": "m#0", "brightness": 80 } ], "missing": [ "gpu", "turbo", 3 ], "restored": [ "docker" ] },
              "lastResults": {
                "ecoQos": { "status": "ok", "detail": "x" },
                "gpu": { "status": "partial", "detail": "y" },
                "brightness": { "status": "ok", "detail": "z" },
                "7": { "status": "ok", "detail": "numeric key" }
              },
              "options": { "blackout": { "keepDocker": true }, "station": { "keepDocker": true }, "reserve": null }
            }
            """;

        var state = StateStore.Deserialize(json)!;

        Assert.Equal(PowerModeKind.Station, state.Mode);
        Assert.Equal(80, state.Snapshot!.Monitors.Single().Brightness);
        Assert.Equal([LeverKind.Gpu], state.Snapshot.Missing);
        Assert.Equal([LeverKind.Docker], state.Snapshot.Restored);
        Assert.Equal([LeverKind.Brightness, LeverKind.Gpu], state.LastResults.Keys.Order());
        Assert.Equal(LeverStatus.Failed, state.LastResults[LeverKind.Gpu].Status);
        Assert.Equal(LeverStatus.Ok, state.LastResults[LeverKind.Brightness].Status);
        Assert.True(state.OptionsFor(PowerModeKind.Station).KeepDocker);
        Assert.Single(state.Options);
    }

    [Theory]
    [InlineData("{\"lastResults\": []}")]
    [InlineData("{\"snapshot\": {\"missing\": {}}}")]
    public void Wrong_collection_shapes_are_still_corrupt(string json)
    {
        Assert.Throws<JsonException>(() => StateStore.Deserialize(json));
    }

    [Fact]
    public void Save_keeps_the_previous_state_as_backup()
    {
        using var dir = new TempDir();
        var store = new StateStore(dir.File("state.json"), new ListLog());

        store.Save(new PersistedState { Mode = PowerModeKind.Station });
        Assert.False(File.Exists(store.BackupPath));
        store.Save(new PersistedState { Mode = PowerModeKind.Reserve });

        Assert.Equal(PowerModeKind.Reserve, store.Load().Mode);
        Assert.Equal(PowerModeKind.Station, StateStore.Deserialize(File.ReadAllText(store.BackupPath))!.Mode);
    }

    [Fact]
    public void Baseline_bookkeeping_round_trips_and_follows_copied_parts()
    {
        var older = RenderFixtures.Baseline();
        older.Missing = [LeverKind.Gpu];
        older.Restored = [LeverKind.Docker];
        older.CoveredApps = ["steam", "EPSDNMON"];
        older.CoveredServices = ["WSearch"];
        older.Processes = [new CapturedProcess("ChatGPT", @"C:\Program Files\WindowsApps\x\ChatGPT.exe", string.Empty, "OpenAI.Codex_2p2nqsd0c76g0!App")];

        var copy = older.Copy();
        Assert.Equal(StateStore.Serialize(new PersistedState { Snapshot = older }), StateStore.Serialize(new PersistedState { Snapshot = copy }));
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0!App", copy.Processes.Single().AppUserModelId);

        var fresh = new Snapshot { Missing = [LeverKind.Apps] };
        fresh.CopyPartFrom(older, LeverKind.Gpu);
        fresh.CopyPartFrom(older, LeverKind.Apps);
        fresh.CopyPartFrom(older, LeverKind.Services);
        Assert.Equal([LeverKind.Gpu], fresh.Missing);
        Assert.Equal(["steam", "EPSDNMON"], fresh.CoveredAppNames());
        Assert.Equal(["WSearch"], fresh.CoveredServiceNames());
        Assert.False(fresh.Owes(LeverKind.Gpu));
        Assert.True(fresh.Owes(LeverKind.Apps));
    }

    [Fact]
    public void A_legacy_snapshot_without_covered_lists_covers_what_it_captured()
    {
        var json = """{ "processes": [ { "name": "steam", "path": "s.exe", "arguments": "" } ], "services": { "WSearch": true } }""";

        var snapshot = JsonSerializer.Deserialize<Snapshot>(json, StateJson.Options)!;

        Assert.Null(snapshot.CoveredApps);
        Assert.Null(snapshot.Processes.Single().AppUserModelId);
        Assert.Equal(["steam"], snapshot.CoveredAppNames());
        Assert.Equal(["WSearch"], snapshot.CoveredServiceNames());
        Assert.Empty(snapshot.Missing);

        var fresh = new Snapshot();
        fresh.CopyPartFrom(snapshot, LeverKind.Apps);
        fresh.CopyPartFrom(snapshot, LeverKind.Services);
        Assert.Null(fresh.CoveredApps);
        Assert.Null(fresh.CoveredServices);
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
