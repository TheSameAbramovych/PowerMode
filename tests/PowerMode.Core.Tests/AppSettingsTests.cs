using System.Text.Json;
using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Settings;

namespace PowerMode.Core.Tests;

public sealed class AppSettingsTests
{
    private static RunningApp Windowed(string name) => new(name, HasWindow: true, IsSystem: false);

    private static RunningApp Background(string name) => new(name, HasWindow: false, IsSystem: false);

    [Fact]
    public void Rows_are_the_config_lists_plus_windowed_or_catalogued_apps_never_system_processes()
    {
        var page = AppSettings.Build(AppConfig.CreateDefault(),
        [
            Windowed("notepad++"),
            Background("discord"),
            Background("someservice"),
            Windowed("explorer"),
            new RunningApp("cleanmgr", HasWindow: true, IsSystem: true),
            Windowed("STEAM"),
        ]);
        var names = page.Rows.Select(r => r.Name).ToList();

        Assert.Contains("notepad++", names);
        Assert.Contains("discord", names);
        Assert.DoesNotContain("someservice", names);
        Assert.DoesNotContain("explorer", names);
        Assert.DoesNotContain("cleanmgr", names);
        Assert.Single(names, n => string.Equals(n, "steam", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("steam", names.Single(n => string.Equals(n, "steam", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(14, names.Count);
        Assert.True(page.PauseWallpaperStation);
        Assert.True(page.PausesWallpaper(PowerModeKind.Reserve));
        Assert.Throws<ArgumentOutOfRangeException>(() => page.PausesWallpaper(PowerModeKind.Grid));
    }

    [Fact]
    public void Rows_with_a_setting_come_first_then_alphabetical()
    {
        var config = AppConfig.CreateDefault();
        var page = AppSettings.Build(config, [Windowed("Zoom"), Windowed("anki")], added: ["Blender", "anki"]);
        var names = page.Rows.Select(r => r.DisplayName).ToList();

        Assert.Equal(["ChatGPT", "Cross Device", "Edge WebView2", "EPSDNMON", "OneDrive", "Phone Link", "Postman Agent", "Slack", "Steam", "Teams", "Telegram", "Viber", "anki", "Blender", "Zoom"], names);
        var steam = page.Rows.Single(r => r.Name == "steam");
        Assert.Equal((AppChoice.Close, AppChoice.Close), (steam.Station, steam.Reserve));
        Assert.Equal(AppChoice.Efficiency, page.Rows.Single(r => r.Name == "Slack").For(PowerModeKind.Reserve));
        Assert.Equal(AppChoice.Keep, page.Rows.Single(r => r.Name == "Slack").For(PowerModeKind.Station));
        Assert.Equal("Postman Agent.exe", page.Rows.Single(r => r.Name == "Postman Agent").FileName);
        Assert.Throws<ArgumentOutOfRangeException>(() => steam.For(PowerModeKind.Grid));
    }

    [Theory]
    [InlineData(AppChoice.Close)]
    [InlineData(AppChoice.Efficiency)]
    [InlineData(AppChoice.Keep)]
    public void A_choice_puts_the_app_in_exactly_one_list_and_keeps_its_spelling(AppChoice choice)
    {
        var mode = AppConfig.CreateDefault().Modes.Reserve;

        AppSettings.SetChoice(mode, "SLACK", choice);

        Assert.Equal(choice, AppSettings.ChoiceOf(mode, "slack"));
        Assert.Equal(choice == AppChoice.Close ? 1 : 0, mode.CloseApps.Count(n => n == "Slack"));
        Assert.Equal(choice == AppChoice.Efficiency ? 1 : 0, mode.EfficiencyApps.Count(n => n == "Slack"));
        Assert.DoesNotContain("SLACK", mode.CloseApps.Concat(mode.EfficiencyApps));
    }

    [Fact]
    public void A_new_app_keeps_the_typed_spelling_and_bad_choices_throw()
    {
        var mode = AppConfig.CreateDefault().Modes.Station;

        AppSettings.SetChoice(mode, "Discord", AppChoice.Efficiency);

        Assert.Equal(["Discord"], mode.EfficiencyApps);
        Assert.Throws<ArgumentOutOfRangeException>(() => AppSettings.SetChoice(mode, "Discord", (AppChoice)9));
    }

    [Theory]
    [InlineData(AppChoice.Keep, AppChoice.Close, new[] { LeverKind.Apps, LeverKind.Efficiency })]
    [InlineData(AppChoice.Efficiency, AppChoice.Close, new[] { LeverKind.Apps, LeverKind.Efficiency })]
    [InlineData(AppChoice.Close, AppChoice.Keep, new[] { LeverKind.Apps, LeverKind.Efficiency })]
    [InlineData(AppChoice.Efficiency, AppChoice.Keep, new[] { LeverKind.Apps, LeverKind.Efficiency })]
    [InlineData(AppChoice.Keep, AppChoice.Efficiency, new[] { LeverKind.Efficiency })]
    [InlineData(AppChoice.Close, AppChoice.Efficiency, new[] { LeverKind.Efficiency })]
    [InlineData(AppChoice.Close, AppChoice.Close, new LeverKind[0])]
    public void A_change_for_the_current_mode_reapplies_these_levers(AppChoice from, AppChoice to, LeverKind[] levers)
    {
        Assert.Equal(levers, AppSettings.ImmediateLevers(PowerModeKind.Station, PowerModeKind.Station, from, to));
    }

    [Theory]
    [InlineData(PowerModeKind.Grid, PowerModeKind.Station)]
    [InlineData(PowerModeKind.Station, PowerModeKind.Reserve)]
    [InlineData(PowerModeKind.Reserve, PowerModeKind.Station)]
    [InlineData(PowerModeKind.Grid, PowerModeKind.Grid)]
    public void A_change_for_another_mode_waits_for_its_next_apply(PowerModeKind current, PowerModeKind changed)
    {
        Assert.Empty(AppSettings.ImmediateLevers(current, changed, AppChoice.Keep, AppChoice.Close));
        Assert.Empty(AppSettings.WallpaperLevers(current, changed));
    }

    [Fact]
    public void Wallpaper_engine_for_the_current_mode_applies_at_once()
    {
        Assert.Equal([LeverKind.WallpaperEngine], AppSettings.WallpaperLevers(PowerModeKind.Reserve, PowerModeKind.Reserve));
    }

    [Theory]
    [InlineData("  Postman Agent  ", "Postman Agent")]
    [InlineData("Discord.exe", "Discord")]
    [InlineData("game.EXE ", "game")]
    public void Typed_process_names_are_trimmed_and_lose_exe(string text, string name)
    {
        Assert.True(AppSettings.TryParseProcessName(text, out var parsed, out var error));
        Assert.Equal(name, parsed);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null, "Type a process name")]
    [InlineData("   ", "Type a process name")]
    [InlineData(".exe", "Type a process name")]
    [InlineData(@"C:\Games\game.exe", "A process name, not a path")]
    [InlineData("bin/game", "A process name, not a path")]
    [InlineData("game?", "A process name, not a path")]
    public void Bad_process_names_are_rejected(string? text, string message)
    {
        Assert.False(AppSettings.TryParseProcessName(text, out _, out var error));
        Assert.Equal(message, error);
    }

    [Fact]
    public void The_catalogue_lists_common_background_apps()
    {
        Assert.True(AppSettings.IsCatalogued("SPOTIFY"));
        Assert.False(AppSettings.IsCatalogued("notepad"));
        Assert.Equal(AppSettings.Catalogue.Count, AppSettings.Catalogue.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}

public sealed class ConfigEditTests
{
    [Fact]
    public void Editing_one_mode_keeps_every_other_key_and_its_spelling()
    {
        const string json = """
            {
              // a user comment
              "Hotkeys": { "grid": "Ctrl+Alt+7" },
              "modes": { "reserve": { "brightnessPercent": 20, "CloseApps": ["steam"] } },
              "unknownKey": 5,
            }
            """;
        var before = ConfigLoader.Parse(json).Config!;
        var after = ConfigLoader.Parse(json).Config!;
        after.Modes.Reserve.CloseApps = ["steam", "Discord"];
        after.Modes.Reserve.EfficiencyApps = ["Slack"];
        after.Modes.Reserve.PauseWallpaperEngine = false;
        after.Modes.Reserve.Scheme.MaxProcessorPercent = 45;

        var edited = ConfigEditor.SetChanges(json, before, after);
        using var document = JsonDocument.Parse(edited);
        var root = document.RootElement;

        Assert.Equal("Ctrl+Alt+7", root.GetProperty("Hotkeys").GetProperty("grid").GetString());
        Assert.Equal(5, root.GetProperty("unknownKey").GetInt32());
        var reserve = root.GetProperty("modes").GetProperty("reserve");
        Assert.Equal(20, reserve.GetProperty("brightnessPercent").GetInt32());
        Assert.Equal(["steam", "Discord"], reserve.GetProperty("CloseApps").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["Slack"], reserve.GetProperty("efficiencyApps").EnumerateArray().Select(e => e.GetString()));
        Assert.False(reserve.GetProperty("pauseWallpaperEngine").GetBoolean());
        Assert.Equal(45, reserve.GetProperty("scheme").GetProperty("maxProcessorPercent").GetInt32());
        Assert.False(reserve.GetProperty("scheme").TryGetProperty("minProcessorPercent", out _));
        Assert.False(root.TryGetProperty("docker", out _));
        Assert.Contains(Environment.NewLine + "  ", edited, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_objects_are_created_and_a_non_object_root_is_rejected()
    {
        var before = AppConfig.CreateDefault();
        var after = AppConfig.CreateDefault();
        after.Modes.Station.CloseApps = ["steam"];
        var edited = ConfigEditor.SetChanges("{}", before, after);
        var config = ConfigLoader.Parse(edited);

        Assert.True(config.IsValid);
        Assert.Equal(["steam"], config.Config!.Modes.Station.CloseApps);
        Assert.Equal(35, config.Config.Modes.Station.BrightnessPercent);
        Assert.Throws<JsonException>(() => ConfigEditor.SetChanges("[1]", before, after));
    }
}

public sealed class ConfigStoreUpdateTests
{
    [Fact]
    public void Updating_a_mode_writes_the_file_and_current_and_the_watcher_ignores_our_write()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        var path = dir.File("config.json");
        using var store = new ConfigStore(path, log);
        Assert.True(store.Load());
        var changed = 0;
        store.Changed += (_, _) => changed++;

        Assert.True(store.UpdateMode(PowerModeKind.Station, m =>
        {
            AppSettings.SetChoice(m, "Discord", AppChoice.Close);
            m.PauseWallpaperEngine = false;
        }, "test"));

        Assert.Equal(["steam", "EPSDNMON", "Discord"], store.Current.Modes.Station.CloseApps);
        Assert.False(store.Current.Modes.Station.PauseWallpaperEngine);
        Assert.Equal(store.Current.Modes.Reserve.CloseApps, AppConfig.CreateDefault().Modes.Reserve.CloseApps);
        Assert.Equal(["steam", "EPSDNMON", "Discord"], ConfigLoader.Parse(File.ReadAllText(path)).Config!.Modes.Station.CloseApps);
        Assert.False(File.Exists(path + ".tmp"));

        store.Reload();
        Assert.Equal(0, changed);

        // Someone else edits the file: that is a real change.
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Discord\"", "\"Zoom\"", StringComparison.Ordinal));
        store.Reload();
        Assert.Equal(1, changed);
        Assert.Contains("Zoom", store.Current.Modes.Station.CloseApps);
    }

    [Fact]
    public void An_invalid_file_on_disk_is_not_overwritten()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        var path = dir.File("config.json");
        using var store = new ConfigStore(path, log);
        Assert.True(store.Load());

        File.WriteAllText(path, """{ "modes": { "station": { "brightnessPercent": 400 } } }""");
        Assert.False(store.UpdateMode(PowerModeKind.Station, m => m.CloseApps.Clear(), "test"));
        Assert.Contains("400", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains(log.Lines, l => l.Contains("config.json would be invalid", StringComparison.Ordinal));

        File.WriteAllText(path, "{ not json");
        Assert.False(store.UpdateMode(PowerModeKind.Station, m => m.CloseApps.Clear(), "test"));
        Assert.Equal("{ not json", File.ReadAllText(path));
        Assert.Equal(["steam", "EPSDNMON"], store.Current.Modes.Station.CloseApps);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.UpdateMode(PowerModeKind.Grid, _ => { }, "test"));
    }

    [Fact]
    public void A_missing_file_is_written_from_the_current_config()
    {
        using var dir = new TempDir();
        var path = dir.File(Path.Combine("sub", "config.json"));
        using var store = new ConfigStore(path, new ListLog());

        Assert.True(store.UpdateMode(PowerModeKind.Reserve, m => AppSettings.SetChoice(m, "Slack", AppChoice.Keep), "test"));

        var saved = ConfigLoader.Parse(File.ReadAllText(path)).Config!;
        Assert.DoesNotContain("Slack", saved.Modes.Reserve.EfficiencyApps);
        Assert.Equal(35, saved.Modes.Station.BrightnessPercent);
    }
}

public sealed class AppSettingChangeTests
{
    /// <summary>Running apps; the lever closes what the target closes and relaunches what PowerMode closed, like ProcessLever.</summary>
    private sealed class AppsMachine(SwitcherHarness harness)
    {
        public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase) { "steam", "Discord" };

        public FakeLever Lever() => new(LeverKind.Apps)
        {
            OnCapture = s =>
            {
                var covered = s.CoveredAppNames();
                var names = harness.Config.AllCloseApps().Where(n => !covered.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
                s.Processes = [.. s.Processes, .. names.Where(Running.Contains).Select(n => new Model.CapturedProcess(n, $@"C:\Apps\{n}.exe", string.Empty))];
                s.CoveredApps = [.. covered, .. names];
            },
            OnPlan = context =>
            {
                var closed = context.State.ClosedApps;
                var actions = context.Target.Apps.Close.Where(Running.Contains).Select(n => new LeverAction($"close {n}", _ =>
                {
                    Running.Remove(n);
                    closed.Add(n);
                    return Task.CompletedTask;
                })).ToList();
                actions.AddRange(context.Target.Apps.Relaunch.Where(p => closed.Contains(p.Name) && !Running.Contains(p.Name)).Select(p => new LeverAction($"relaunch {p.Name}", _ =>
                {
                    Running.Add(p.Name);
                    closed.Remove(p.Name);
                    return Task.CompletedTask;
                })));
                return LeverPlan.Of(actions);
            },
        };
    }

    [Fact]
    public async Task Changes_for_the_current_mode_close_relaunch_and_leave_closed_as_decided()
    {
        using var harness = new SwitcherHarness([]);
        var apps = new AppsMachine(harness);
        using var switcher = new Switching.ModeSwitcher([apps.Lever(), new FakeLever(LeverKind.Efficiency)], harness.Store, () => harness.Config, () => true, harness.Time, harness.Log);
        await switcher.SwitchAsync(PowerModeKind.Station);
        Assert.DoesNotContain("steam", apps.Running);

        // Keep -> Close: captured for Grid, closed now, recorded.
        AppSettings.SetChoice(harness.Config.Modes.Station, "Discord", AppChoice.Close);
        var close = await switcher.ApplySettingChangeAsync(m => AppSettings.ImmediateLevers(m, PowerModeKind.Station, AppChoice.Keep, AppChoice.Close));
        Assert.Equal(0, close!.FailedCount);
        Assert.DoesNotContain("Discord", apps.Running);
        Assert.Contains("Discord", harness.Reload().ClosedApps);
        Assert.Contains(harness.Reload().Snapshot!.Processes, p => p.Name == "Discord");

        // Close -> Keep: relaunched because PowerMode closed it.
        AppSettings.SetChoice(harness.Config.Modes.Station, "Discord", AppChoice.Keep);
        await switcher.ApplySettingChangeAsync(m => AppSettings.ImmediateLevers(m, PowerModeKind.Station, AppChoice.Close, AppChoice.Keep));
        Assert.Contains("Discord", apps.Running);
        Assert.DoesNotContain("Discord", harness.Reload().ClosedApps);

        // Close -> Efficiency: Apps is not re-applied, so Steam stays closed until the next apply.
        AppSettings.SetChoice(harness.Config.Modes.Station, "steam", AppChoice.Efficiency);
        var efficiency = await switcher.ApplySettingChangeAsync(m => AppSettings.ImmediateLevers(m, PowerModeKind.Station, AppChoice.Close, AppChoice.Efficiency));
        Assert.Equal([LeverKind.Efficiency], efficiency!.Results.Keys);
        Assert.DoesNotContain("steam", apps.Running);
        Assert.Contains("steam", harness.Reload().ClosedApps);

        // A change for Reserve while in Station waits for Reserve.
        AppSettings.SetChoice(harness.Config.Modes.Reserve, "Discord", AppChoice.Close);
        Assert.Null(await switcher.ApplySettingChangeAsync(m => AppSettings.ImmediateLevers(m, PowerModeKind.Reserve, AppChoice.Keep, AppChoice.Close)));
        Assert.Contains("Discord", apps.Running);

        // Grid restores what PowerMode closed, Discord included.
        await switcher.SwitchAsync(PowerModeKind.Grid);
        Assert.Contains("steam", apps.Running);
        Assert.Null(await switcher.ApplySettingChangeAsync(m => AppSettings.ImmediateLevers(m, PowerModeKind.Station, AppChoice.Keep, AppChoice.Close)));
    }
}

public sealed class NotAppliedFeedbackTests
{
    [Fact]
    public void Only_a_failed_lever_turns_saved_into_a_warning()
    {
        Assert.Null(SettingsFeedback.NotAppliedMessage((Switching.SwitchOutcome?)null));
        var ok = new Switching.SwitchOutcome(PowerModeKind.Station, new Dictionary<Levers.LeverKind, Levers.LeverResult>
        {
            [Levers.LeverKind.PowerScheme] = Levers.LeverResult.Ok(Levers.LeverResult.AlreadySet),
        });
        Assert.Null(SettingsFeedback.NotAppliedMessage(ok));

        var failed = new Switching.SwitchOutcome(PowerModeKind.Station, new Dictionary<Levers.LeverKind, Levers.LeverResult>
        {
            [Levers.LeverKind.PowerScheme] = Levers.LeverResult.Ok(Levers.LeverResult.AlreadySet),
            [Levers.LeverKind.Gpu] = Levers.LeverResult.Failed(Levers.LeverResult.NeedsAdministrator, needsElevation: true),
            [Levers.LeverKind.Services] = Levers.LeverResult.Failed(Levers.LeverResult.NeedsAdministrator, needsElevation: true),
        });
        Assert.Equal("Saved, but not applied now: Needs administrator. The main page shows the lever.", SettingsFeedback.NotAppliedMessage(failed));
        Assert.Equal("Saved, but not applied now: boom. The main page shows the lever.", SettingsFeedback.NotAppliedMessage("boom"));
    }
}
