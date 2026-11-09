using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;
using PowerMode.Core.Planning;
using PowerMode.Core.Presentation;
using PowerMode.Core.Settings;
using PowerMode.Core.State;
using PowerMode.Core.Switching;

namespace PowerMode.Core.Tests;

public sealed class DisplayDecisionTests
{
    private static readonly DisplayTopology TwoOn = new(2, "AAAA");

    [Fact]
    public void Primary_only_acts_only_with_secondary_displays_on()
    {
        var off = new TopologyTarget(true, null);

        Assert.Equal(TopologyAction.SkipOneDisplay, DisplayDecisions.Decide(off, 1, 1));
        Assert.Equal(TopologyAction.PrimaryOnly, DisplayDecisions.Decide(off, 2, 2));
        Assert.Equal(TopologyAction.Done, DisplayDecisions.Decide(off, 2, 1));
    }

    [Fact]
    public void Restore_acts_only_while_fewer_displays_are_on_than_saved()
    {
        var on = new TopologyTarget(false, TwoOn);

        Assert.Equal(TopologyAction.Restore, DisplayDecisions.Decide(on, 2, 1));
        Assert.Equal(TopologyAction.Done, DisplayDecisions.Decide(on, 2, 2));
        Assert.Equal(TopologyAction.SkipOneDisplay, DisplayDecisions.Decide(on, 1, 1));
        Assert.Equal(TopologyAction.Done, DisplayDecisions.Decide(new TopologyTarget(false, null), 2, 1));
    }

    [Fact]
    public void A_display_without_the_rate_falls_back_to_its_lowest_rate_from_50_hz()
    {
        Assert.Equal((60, false), DisplayDecisions.ChooseRefresh([59, 60, 144], 60));
        Assert.Equal((59, false), DisplayDecisions.ChooseRefresh([59, 75], 60));
        Assert.Equal((75, true), DisplayDecisions.ChooseRefresh([75, 100, 144], 60));
        Assert.Equal((50, true), DisplayDecisions.ChooseRefresh([30, 48, 50, 75], 60));
        Assert.Null(DisplayDecisions.ChooseRefresh([30, 48], 60));
        Assert.Null(DisplayDecisions.ChooseRefresh([], 60));
    }

    [Fact]
    public void Planner_turns_secondary_displays_off_in_reserve_and_back_on_after()
    {
        var config = AppConfig.CreateDefault();
        var snapshot = RenderFixtures.Baseline();
        snapshot.Topology = TwoOn;

        Assert.Equal(new TopologyTarget(true, null), TargetPlanner.Plan(PowerModeKind.Reserve, config, snapshot).Topology);
        Assert.Equal(new TopologyTarget(false, TwoOn), TargetPlanner.Plan(PowerModeKind.Station, config, snapshot).Topology);
        Assert.Equal(new TopologyTarget(false, TwoOn), TargetPlanner.Plan(PowerModeKind.Grid, config, snapshot).Topology);
        Assert.Null(TargetPlanner.Plan(PowerModeKind.Station, config, null).Topology);

        snapshot.Restored = [LeverKind.SecondaryDisplays];
        Assert.Null(TargetPlanner.Plan(PowerModeKind.Grid, config, snapshot).Topology);

        var older = new Snapshot { Topology = TwoOn };
        var copy = new Snapshot();
        copy.CopyPartFrom(older, LeverKind.SecondaryDisplays);
        Assert.Equal(TwoOn, copy.Topology);
        Assert.True(config.Modes.Reserve.SecondaryDisplaysOff);
        Assert.False(config.Modes.Station.SecondaryDisplaysOff);
    }

    [Fact]
    public void Several_monitors_get_a_line_each_and_off_when_secondary_displays_are_off()
    {
        var config = AppConfig.CreateDefault();
        var live = new LiveValues(Monitors: [new MonitorLive("M27Q2", 35, 59), new MonitorLive("T24i-30", 35, 60)]);
        var station = Build(PowerModeKind.Station, config, live);
        var monitor = station.Rows.Single(r => r.Row == LeverRow.Monitor);

        Assert.Equal(["M27Q2 · 35 % · 60 Hz", "T24i-30 · 35 % · 60 Hz"], monitor.Lines);
        Assert.Equal("35 % · 60 Hz" + Environment.NewLine + "35 % · 60 Hz", monitor.Live);

        var offLive = new LiveValues(Monitors: [new MonitorLive("M27Q2", 10, 60), new MonitorLive("T24i-30", null, null, Active: false)]);
        var reserve = Build(PowerModeKind.Reserve, config, offLive).Rows.Single(r => r.Row == LeverRow.Monitor);
        Assert.Equal(["M27Q2 · 10 % · 60 Hz", "T24i-30 · Off"], reserve.Lines);
        Assert.Equal("10 % · 60 Hz" + Environment.NewLine + "Off", reserve.Live);

        // One monitor: no name, as before.
        Assert.Equal(["35 % · 60 Hz"], Build(PowerModeKind.Station, config, new LiveValues(BrightnessPercent: 35, RefreshHz: 60)).Rows.Single(r => r.Row == LeverRow.Monitor).Lines);
        Assert.Null(Build(PowerModeKind.Station, config, LiveValues.Empty).Rows.Single(r => r.Row == LeverRow.Monitor).Live);
    }

    [Fact]
    public void The_estimate_sums_the_monitors_that_are_on()
    {
        var two = new LiveValues(Monitors: [new MonitorLive("A", 80, 200), new MonitorLive("B", 50, 60), new MonitorLive("C", 50, 60, Active: false)]).ToPowerInputs();

        Assert.Equal(2, two.Monitors.Count);
        Assert.Single(LiveValues.Empty.ToPowerInputs().Monitors);
    }

    [Fact]
    public void Missing_hardware_and_software_is_named_in_the_rows()
    {
        var config = AppConfig.CreateDefault();
        var bare = new MachineInfo("Intel Core i5-1135G7", 2400, null, ["Built-in display"], [60], false, false, false, "1.0.0");
        var station = Build(PowerModeKind.Station, config, LiveValues.Empty, bare);

        Assert.Equal([MachineInfo.NoNvidiaGpu], station.Rows.Single(r => r.Row == LeverRow.Gpu).Lines);
        Assert.Equal([MachineInfo.DockerNotInstalled], station.Rows.Single(r => r.Row == LeverRow.Docker).Lines);
        Assert.False(station.Rows.Single(r => r.Row == LeverRow.Docker).ShowKeepDocker);
        Assert.DoesNotContain(station.Rows.Single(r => r.Row == LeverRow.Apps).Lines, l => l.Contains("Wallpaper", StringComparison.Ordinal));
        Assert.DoesNotContain(Build(PowerModeKind.Grid, config, LiveValues.Empty, bare, RenderFixtures.Baseline()).Rows.Single(r => r.Row == LeverRow.Apps).Lines,
            l => l.Contains("Wallpaper", StringComparison.Ordinal));

        Assert.Contains(("GPU", "None (no NVIDIA GPU detected)"), bare.Facts());
        Assert.Contains(("Rights", "Standard user"), bare.Facts());
        Assert.Contains(("Docker Desktop", "Not installed"), bare.Facts());
        Assert.Contains(("Monitors", "None detected"), (bare with { Monitors = [] }).Facts());
        Assert.Contains(("GPU", "NVIDIA GPU · 60–132 W"), MachineInfo.Assumed.Facts());
    }

    [Fact]
    public void A_skip_plan_reports_its_reason()
    {
        var plan = LeverPlan.Skip(MachineInfo.NoNvidiaGpu);

        Assert.True(plan.NotOwned);
        Assert.Equal(MachineInfo.NoNvidiaGpu, plan.SkipReason);
    }

    private static FlyoutState Build(PowerModeKind mode, AppConfig config, LiveValues live, MachineInfo? machine = null, Snapshot? snapshot = null) =>
        FlyoutStateBuilder.Build(new FlyoutInput(
            TargetPlanner.Plan(mode, config, snapshot ?? RenderFixtures.Baseline()),
            new Dictionary<LeverKind, LeverResult>(),
            new HashSet<LeverKind>(),
            live,
            null,
            RenderFixtures.Now,
            true,
            false,
            false,
            config.Hotkeys,
            Machine: machine));
}

public sealed class StartAndShutdownTests
{
    [Theory]
    [InlineData(PowerModeKind.Station, true, StartAction.RestoreGrid)]
    [InlineData(PowerModeKind.Reserve, true, StartAction.RestoreGrid)]
    [InlineData(PowerModeKind.Station, false, StartAction.Reapply)]
    public void A_saved_saving_mode_starts_in_grid_when_configured(PowerModeKind mode, bool startInGrid, StartAction action)
    {
        Assert.Equal(action, StartAndShutdown.Decide(new PersistedState { Mode = mode, Snapshot = new Snapshot() }, startInGrid));
    }

    [Fact]
    public void Grid_starts_do_nothing_unless_a_restore_is_owed()
    {
        Assert.Equal(StartAction.Nothing, StartAndShutdown.Decide(new PersistedState(), true));
        Assert.Equal(StartAction.Reapply, StartAndShutdown.Decide(new PersistedState { Snapshot = new Snapshot() }, true));
        Assert.True(StartAndShutdown.RestoresAtShutdown(PowerModeKind.Reserve, true));
        Assert.False(StartAndShutdown.RestoresAtShutdown(PowerModeKind.Reserve, false));
        Assert.False(StartAndShutdown.RestoresAtShutdown(PowerModeKind.Grid, true));
        Assert.True(AppConfig.CreateDefault().GridOnShutdown);
        Assert.True(AppConfig.CreateDefault().StartInGrid);
    }

    [Fact]
    public async Task Shutdown_restores_only_the_fast_levers_and_keeps_the_rest_owed()
    {
        var monitor = new BrightnessMachine();
        var apps = new FakeLever(LeverKind.Apps) { OnPlan = c => c.Target.Apps.Relaunch.Count > 0 ? LeverPlan.Of([new LeverAction("relaunch", _ => Task.CompletedTask)]) : LeverPlan.None };
        using var harness = new SwitcherHarness([monitor.Lever(), apps]);
        await harness.Switcher.SwitchAsync(PowerModeKind.Reserve);
        harness.Switcher.State.Snapshot!.Processes = [new Model.CapturedProcess("steam", @"C:\steam.exe", string.Empty)];

        Assert.True(await harness.Switcher.ShutdownToGridAsync(StartAndShutdown.ShutdownBudget));

        Assert.Equal(80, monitor.Value);
        var saved = harness.Reload();
        Assert.Equal(PowerModeKind.Grid, saved.Mode);
        Assert.Contains(LeverKind.Brightness, saved.Snapshot!.Restored);
        Assert.DoesNotContain(LeverKind.Apps, saved.Snapshot.Restored);
        Assert.True(harness.Switcher.NeedsReapplyOnStart);
        Assert.False(await harness.Switcher.ShutdownToGridAsync(StartAndShutdown.ShutdownBudget));
        Assert.Contains(harness.Log.Lines, l => l.Contains("Shutdown: restoring Grid from Reserve", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shutdown_gives_up_when_a_switch_holds_the_gate_past_the_budget()
    {
        var release = new TaskCompletionSource();
        var slow = new FakeLever(LeverKind.Gpu)
        {
            OnPlan = c => c.Target.Gpu is null ? LeverPlan.None : LeverPlan.Of([new LeverAction("slow", _ => release.Task)]),
        };
        using var harness = new SwitcherHarness([slow]);
        var switching = harness.Switcher.SwitchAsync(PowerModeKind.Station);

        Assert.False(await harness.Switcher.ShutdownToGridAsync(TimeSpan.FromMilliseconds(50)));

        release.SetResult();
        await switching;
        Assert.Contains(harness.Log.Lines, l => l.Contains("Shutdown: a switch is still running", StringComparison.Ordinal));
    }
}

public sealed class CpuProfileTests
{
    [Theory]
    [InlineData("AMD Ryzen 7 2700 Eight-Core Processor", 65)]
    [InlineData("AMD Ryzen 5 3600X 6-Core Processor", 95)]
    [InlineData("AMD Ryzen 9 5900X 12-Core Processor", 105)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", 105)]
    [InlineData("AMD Ryzen 7 5700U with Radeon Graphics", 15)]
    [InlineData("AMD Ryzen 9 6900HS with Radeon Graphics", 35)]
    [InlineData("AMD Ryzen 5 5600G with Radeon Graphics", 65)]
    [InlineData("Intel(R) Core(TM) i9-13900K", 125)]
    [InlineData("Intel(R) Core(TM) i5-12400F", 65)]
    [InlineData("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", 65)]
    [InlineData("12th Gen Intel(R) Core(TM) i7-12700H", 45)]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13980HX", 55)]
    [InlineData("Intel(R) Core(TM) i5-1235U", 15)]
    [InlineData("Apple M4", 65)]
    [InlineData("", 65)]
    [InlineData(null, 65)]
    public void Tdp_is_guessed_from_the_name(string? name, int tdp)
    {
        Assert.Equal(tdp, CpuProfiles.GuessTdpW(name));
    }

    [Fact]
    public void The_estimate_scales_with_the_tdp_and_takes_the_rated_clock()
    {
        var tuned = CpuProfiles.Tune(new Power.PowerConfig(), "Intel(R) Core(TM) i9-13900K", 3000);

        Assert.Equal(28.8, tuned.CpuIdleW, 1);
        Assert.Equal(86.5, tuned.CpuSpanW, 1);
        Assert.Equal(3.0, tuned.CpuBaseGhz);
        Assert.Equal(3.2, CpuProfiles.Tune(new Power.PowerConfig(), "AMD Ryzen 7 2700", null).CpuBaseGhz);
        Assert.Equal(45, CpuProfiles.Tune(new Power.PowerConfig(), "AMD Ryzen 7 2700", 99).CpuSpanW);
    }
}

public sealed class LeverSettingsTests
{
    private static readonly MachineInfo Machine = MachineInfo.Assumed with { Monitors = ["M27Q2", "T24i-30"], RefreshRates = [144, 60, 59, 60] };

    [Fact]
    public void Rows_show_both_modes_with_their_choices()
    {
        var rows = LeverSettings.Build(AppConfig.CreateDefault(), Machine);

        Assert.Equal(
            [LeverSetting.CpuMaxPercent, LeverSetting.Boost, LeverSetting.GpuLimitW, LeverSetting.BrightnessPercent, LeverSetting.RefreshHz,
             LeverSetting.SecondaryDisplays, LeverSetting.DisplayOffMinutes, LeverSetting.HibernateMinutes, LeverSetting.Docker, LeverSetting.WallpaperEngine],
            rows.Select(r => r.Setting));
        var cpu = rows.Single(r => r.Setting == LeverSetting.CpuMaxPercent);
        Assert.Equal((80, 50), (cpu.Station, cpu.Reserve));
        Assert.Equal(17, cpu.Options.Count);
        Assert.Equal("80 %", cpu.Options.Single(o => o.Value == 80).Label);
        var gpu = rows.Single(r => r.Setting == LeverSetting.GpuLimitW);
        Assert.True(gpu.IsNumeric);
        Assert.Equal("60–132 W, watts", gpu.Note);
        Assert.Equal(["Keep", "59 Hz", "60 Hz", "144 Hz"], rows.Single(r => r.Setting == LeverSetting.RefreshHz).Options.Select(o => o.Label));
        Assert.Equal((0, 1), (rows.Single(r => r.Setting == LeverSetting.SecondaryDisplays).Station, rows.Single(r => r.Setting == LeverSetting.SecondaryDisplays).Reserve));
        var hibernate = rows.Single(r => r.Setting == LeverSetting.HibernateMinutes);
        Assert.Equal((30, 10), (hibernate.Station, hibernate.Reserve));
        Assert.Equal("Never", hibernate.Options[0].Label);
        Assert.Equal("30 min", hibernate.Options.Single(o => o.Value == 30).Label);
        Assert.Equal(80, rows[0].For(PowerModeKind.Station));
        Assert.Throws<ArgumentOutOfRangeException>(() => rows[0].For(PowerModeKind.Grid));
    }

    [Fact]
    public void Missing_parts_disable_or_hide_their_rows_and_odd_config_values_stay_visible()
    {
        var config = AppConfig.CreateDefault();
        config.Modes.Station.Scheme.MaxProcessorPercent = 83;
        config.Modes.Reserve.Scheme.DisplayOffSeconds = 7 * 60;
        var bare = Machine with { Gpu = null, DockerInstalled = false, WallpaperEngineInstalled = false, Monitors = ["Panel"] };
        var rows = LeverSettings.Build(config, bare);

        Assert.False(rows.Single(r => r.Setting == LeverSetting.GpuLimitW).Enabled);
        Assert.Equal("No NVIDIA GPU detected", rows.Single(r => r.Setting == LeverSetting.GpuLimitW).Note);
        Assert.False(rows.Single(r => r.Setting == LeverSetting.Docker).Enabled);
        Assert.Equal("One display connected", rows.Single(r => r.Setting == LeverSetting.SecondaryDisplays).Note);
        Assert.DoesNotContain(rows, r => r.Setting == LeverSetting.WallpaperEngine);
        Assert.Contains(rows.Single(r => r.Setting == LeverSetting.CpuMaxPercent).Options, o => o is { Value: 83, Label: "83 %" });
        Assert.Contains(rows.Single(r => r.Setting == LeverSetting.DisplayOffMinutes).Options, o => o is { Value: 7, Label: "7 min" });
    }

    [Theory]
    [InlineData(LeverSetting.CpuMaxPercent, 83, 85)]
    [InlineData(LeverSetting.CpuMaxPercent, 5, 20)]
    [InlineData(LeverSetting.CpuMaxPercent, null, 100)]
    [InlineData(LeverSetting.GpuLimitW, 40, 60)]
    [InlineData(LeverSetting.GpuLimitW, 500, 132)]
    [InlineData(LeverSetting.GpuLimitW, null, null)]
    [InlineData(LeverSetting.BrightnessPercent, 140, 100)]
    [InlineData(LeverSetting.BrightnessPercent, null, null)]
    [InlineData(LeverSetting.RefreshHz, 75, 75)]
    [InlineData(LeverSetting.RefreshHz, null, null)]
    [InlineData(LeverSetting.Boost, 1, 1)]
    [InlineData(LeverSetting.SecondaryDisplays, 1, 1)]
    [InlineData(LeverSetting.DisplayOffMinutes, 10, 10)]
    [InlineData(LeverSetting.HibernateMinutes, -3, 0)]
    [InlineData(LeverSetting.Docker, 0, 0)]
    [InlineData(LeverSetting.WallpaperEngine, 0, 0)]
    public void Changes_are_clamped_to_what_the_lever_accepts(LeverSetting setting, int? value, int? stored)
    {
        var mode = AppConfig.CreateDefault().Modes.Station;

        Assert.Equal(stored, LeverSettings.Apply(mode, setting, value, MachineInfo.Assumed.Gpu));
        Assert.Equal(stored, LeverSettings.ValueOf(mode, setting));
    }

    [Fact]
    public void Odd_values_and_unknown_settings()
    {
        var mode = AppConfig.CreateDefault().Modes.Station;
        mode.Scheme.MinProcessorPercent = 60;

        LeverSettings.Apply(mode, LeverSetting.CpuMaxPercent, 40, null);
        Assert.Equal(40, mode.Scheme.MinProcessorPercent);
        Assert.Equal(2000, LeverSettings.Apply(mode, LeverSetting.GpuLimitW, 5000, null));
        Assert.Equal(600, LeverSettings.Apply(mode, LeverSetting.DisplayOffMinutes, 10, null) * 60);
        Assert.Throws<ArgumentOutOfRangeException>(() => LeverSettings.Apply(mode, (LeverSetting)99, 1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => LeverSettings.ValueOf(mode, (LeverSetting)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => LeverSettings.ImmediateLevers(PowerModeKind.Station, PowerModeKind.Station, (LeverSetting)99));
    }

    [Theory]
    [InlineData("90", true, 90)]
    [InlineData(" 120 W ", true, 120)]
    [InlineData("", true, null)]
    [InlineData("abc", false, null)]
    [InlineData("0", false, null)]
    [InlineData("-5", false, null)]
    public void Typed_gpu_watts_are_parsed(string text, bool ok, int? watts)
    {
        Assert.Equal(ok, LeverSettings.TryParseWatts(text, out var parsed));
        Assert.Equal(watts, parsed);
    }

    [Fact]
    public void Changes_for_the_current_mode_reapply_their_levers()
    {
        Assert.Equal([LeverKind.PowerScheme], LeverSettings.ImmediateLevers(PowerModeKind.Station, PowerModeKind.Station, LeverSetting.CpuMaxPercent));
        Assert.Equal([LeverKind.PowerScheme], LeverSettings.ImmediateLevers(PowerModeKind.Station, PowerModeKind.Station, LeverSetting.HibernateMinutes));
        Assert.Equal([LeverKind.Gpu], LeverSettings.ImmediateLevers(PowerModeKind.Reserve, PowerModeKind.Reserve, LeverSetting.GpuLimitW));
        Assert.Equal([LeverKind.Brightness], LeverSettings.ImmediateLevers(PowerModeKind.Reserve, PowerModeKind.Reserve, LeverSetting.BrightnessPercent));
        Assert.Equal([LeverKind.DisplayMode, LeverKind.Brightness], LeverSettings.ImmediateLevers(PowerModeKind.Reserve, PowerModeKind.Reserve, LeverSetting.RefreshHz));
        Assert.Equal([LeverKind.SecondaryDisplays, LeverKind.DisplayMode, LeverKind.Brightness], LeverSettings.ImmediateLevers(PowerModeKind.Reserve, PowerModeKind.Reserve, LeverSetting.SecondaryDisplays));
        Assert.Equal([LeverKind.Docker], LeverSettings.ImmediateLevers(PowerModeKind.Station, PowerModeKind.Station, LeverSetting.Docker));
        Assert.Equal([LeverKind.WallpaperEngine], LeverSettings.ImmediateLevers(PowerModeKind.Station, PowerModeKind.Station, LeverSetting.WallpaperEngine));
        Assert.Empty(LeverSettings.ImmediateLevers(PowerModeKind.Station, PowerModeKind.Reserve, LeverSetting.Docker));
        Assert.Empty(LeverSettings.ImmediateLevers(PowerModeKind.Grid, PowerModeKind.Grid, LeverSetting.Docker));
    }

    [Fact]
    public void A_whole_config_update_writes_only_what_changed()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        using var store = new ConfigStore(path, new ListLog());
        Assert.True(store.Load());

        Assert.True(store.Update(c => c.FallbackScheme = Guid.Parse("9897998c-92de-4669-853f-b7cd3ecb2790"), "fallback scheme"));
        Assert.True(store.UpdateMode(PowerModeKind.Reserve, m => LeverSettings.Apply(m, LeverSetting.GpuLimitW, 70, null), "GPU limit 70 W"));

        var saved = ConfigLoader.Parse(File.ReadAllText(path)).Config!;
        Assert.Equal(Guid.Parse("9897998c-92de-4669-853f-b7cd3ecb2790"), saved.FallbackScheme);
        Assert.Equal(70, saved.Modes.Reserve.GpuLimitWatts);
        Assert.False(store.Update(c => c.Flyout.Margin = 999, "margin"));
        Assert.Equal(12, store.Current.Flyout.Margin);
    }
}
