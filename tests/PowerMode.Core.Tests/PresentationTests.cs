using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;
using PowerMode.Core.Planning;
using PowerMode.Core.Presentation;
using PowerMode.Core.Switching;

namespace PowerMode.Core.Tests;

public sealed class PresentationTests
{
    private static readonly AppConfig Config = AppConfig.CreateDefault();
    private static readonly DateTimeOffset Now = RenderFixtures.Now;

    private static FlyoutState Build(
        PowerModeKind mode,
        Dictionary<LeverKind, LeverResult>? results = null,
        HashSet<LeverKind>? applying = null,
        LiveValues? live = null,
        bool elevated = true,
        DateTimeOffset? since = null) =>
        FlyoutStateBuilder.Build(new FlyoutInput(
            TargetPlanner.Plan(mode, Config, RenderFixtures.Baseline()),
            results ?? [],
            applying ?? [],
            live ?? LiveValues.Empty,
            since ?? RenderFixtures.Since,
            Now,
            elevated,
            KeepDocker: false,
            StartWithWindows: false,
            Config.Hotkeys));

    private static RowState Row(FlyoutState state, LeverRow row) => state.Rows.Single(r => r.Row == row);

    [Fact]
    public void Rows_come_in_the_fixed_order()
    {
        var state = Build(PowerModeKind.Station);

        Assert.Equal(["CPU", "GPU", "Monitor", "Display", "Docker", "Apps", "Efficiency", "Services"], state.Rows.Select(r => r.Title));
    }

    [Fact]
    public void Station_rows_say_what_the_mode_set()
    {
        var state = Build(PowerModeKind.Station);

        Assert.Equal(["Boost off · max 80 %"], Row(state, LeverRow.Cpu).Lines);
        Assert.Equal(["Limit 90 W"], Row(state, LeverRow.Gpu).Lines);
        Assert.Equal(["35 % · 60 Hz"], Row(state, LeverRow.Monitor).Lines);
        Assert.Equal(["Off after 5 min · Hibernate 30 min"], Row(state, LeverRow.Display).Lines);
        Assert.Equal(["Stopped · was 6 containers"], Row(state, LeverRow.Docker).Lines);
        Assert.Equal(["Closed: Steam, EPSDNMON", "Wallpaper Engine paused"], Row(state, LeverRow.Apps).Lines);
        Assert.Equal(["Off"], Row(state, LeverRow.Efficiency).Lines);
        Assert.Equal(["Stopped: Search"], Row(state, LeverRow.Services).Lines);
        Assert.True(Row(state, LeverRow.Docker).ShowKeepDocker);
    }

    [Fact]
    public void Reserve_lists_every_name_in_full()
    {
        var state = Build(PowerModeKind.Reserve);

        Assert.Equal(["Slack, Telegram, Edge WebView2, Phone Link, Cross Device"], Row(state, LeverRow.Efficiency).Lines);
        Assert.Contains("Postman Agent", Row(state, LeverRow.Apps).Lines[0], StringComparison.Ordinal);
        Assert.Equal(["Stopped: Search, SysMain, Telemetry, Print Spooler, MongoDB, MySQL80, postgresql-x64-18"], Row(state, LeverRow.Services).Lines);
        Assert.DoesNotContain(state.Rows, r => r.Lines.Any(l => l.Contains('+', StringComparison.Ordinal)));
        Assert.False(Row(state, LeverRow.Docker).ShowKeepDocker);
    }

    [Fact]
    public void Grid_rows_describe_the_users_own_settings()
    {
        var state = Build(PowerModeKind.Grid);

        Assert.Equal(["Your power scheme"], Row(state, LeverRow.Cpu).Lines);
        Assert.Equal(["Running · 6 containers"], Row(state, LeverRow.Docker).Lines);
        Assert.Equal(["Nothing closed", "Wallpaper Engine playing"], Row(state, LeverRow.Apps).Lines);
        Assert.StartsWith("Running: ", Row(state, LeverRow.Services).Lines[0], StringComparison.Ordinal);
        var bare = FlyoutStateBuilder.Build(new FlyoutInput(TargetPlanner.Plan(PowerModeKind.Grid, Config, null), new Dictionary<LeverKind, LeverResult>(), new HashSet<LeverKind>(), LiveValues.Empty, null, Now, true, false, false, Config.Hotkeys));
        Assert.Equal("Grid", bare.StatusLine);
        Assert.Equal(["As you left it"], Row(bare, LeverRow.Docker).Lines);
        Assert.Equal(["As you left them"], Row(bare, LeverRow.Services).Lines);
        Assert.Equal(["Nothing closed"], Row(bare, LeverRow.Apps).Lines);
    }

    [Fact]
    public void Live_values_are_formatted_per_row()
    {
        var state = Build(PowerModeKind.Station, live: new LiveValues(3.14, 12.4, 9.4, 35, 60, false, 0));

        Assert.Equal("3.1 GHz · 12 %", Row(state, LeverRow.Cpu).Live);
        Assert.Equal("9 W", Row(state, LeverRow.Gpu).Live);
        Assert.Equal("35 % · 60 Hz", Row(state, LeverRow.Monitor).Live);
        Assert.Equal("engine stopped", Row(state, LeverRow.Docker).Live);
        Assert.Null(Row(state, LeverRow.Display).Live);

        var other = Build(PowerModeKind.Station, live: new LiveValues(CpuGhz: 2.0, RefreshHz: 60, DockerEngineRunning: true, DockerContainersRunning: 6));
        Assert.Equal("2.0 GHz", Row(other, LeverRow.Cpu).Live);
        Assert.Equal("60 Hz", Row(other, LeverRow.Monitor).Live);
        Assert.Equal("6 running", Row(other, LeverRow.Docker).Live);
        Assert.Null(Row(other, LeverRow.Gpu).Live);
    }

    [Fact]
    public void Status_line_and_glyphs_follow_results()
    {
        var ok = Build(PowerModeKind.Station, results: new() { [LeverKind.PowerScheme] = LeverResult.Ok("x") });
        Assert.Equal("Station · since 14:32", ok.StatusLine);
        Assert.Equal(StatusTone.Normal, ok.StatusTone);
        Assert.Equal(RowGlyph.Ok, Row(ok, LeverRow.Cpu).Glyph);
        Assert.Equal(RowGlyph.Ok, Row(ok, LeverRow.Display).Glyph);
        Assert.Equal(RowGlyph.None, Row(ok, LeverRow.Gpu).Glyph);

        var applying = Build(PowerModeKind.Station, applying: [LeverKind.Docker]);
        Assert.Equal("Applying Station…", applying.StatusLine);
        Assert.True(applying.IsApplying);
        Assert.Equal(RowGlyph.Applying, Row(applying, LeverRow.Docker).Glyph);

        var failed = Build(PowerModeKind.Reserve, results: new()
        {
            [LeverKind.Docker] = LeverResult.Failed("Docker is not installed"),
            [LeverKind.Brightness] = LeverResult.Ok("x"),
            [LeverKind.DisplayMode] = LeverResult.Failed("refused"),
        });
        Assert.Equal("Reserve · 2 levers failed", failed.StatusLine);
        Assert.Equal(StatusTone.Warning, failed.StatusTone);
        Assert.Equal("Docker is not installed", Row(failed, LeverRow.Docker).FailureReason);
        Assert.Equal(RowGlyph.Failed, Row(failed, LeverRow.Monitor).Glyph);
        Assert.Null(failed.Banner);

        var one = Build(PowerModeKind.Reserve, results: new() { [LeverKind.Docker] = LeverResult.Failed("x") });
        Assert.Equal("Reserve · 1 lever failed", one.StatusLine);

        var skipped = Build(PowerModeKind.Station, results: new() { [LeverKind.Efficiency] = LeverResult.Skipped(LeverResult.NotOwned) });
        Assert.Equal(RowGlyph.None, Row(skipped, LeverRow.Efficiency).Glyph);
    }

    [Fact]
    public void Since_on_another_day_shows_the_date()
    {
        var state = Build(PowerModeKind.Station, since: Now.AddDays(-1));

        Assert.Equal("Station · since 8 Nov 15:05", state.StatusLine);
    }

    [Fact]
    public void Elevation_banner_only_when_not_elevated_and_a_lever_needed_it()
    {
        var results = new Dictionary<LeverKind, LeverResult>
        {
            [LeverKind.Gpu] = LeverResult.Failed(LeverResult.NeedsAdministrator, true),
            [LeverKind.Services] = LeverResult.Failed(LeverResult.NeedsAdministrator, true),
        };

        Assert.Equal("GPU limit and services need administrator rights.", Build(PowerModeKind.Station, results, elevated: false).Banner);
        Assert.Null(Build(PowerModeKind.Station, results, elevated: true).Banner);
        Assert.Null(Build(PowerModeKind.Station, results, applying: [LeverKind.Gpu], elevated: false).Banner);

        var single = new Dictionary<LeverKind, LeverResult> { [LeverKind.Services] = LeverResult.Failed(LeverResult.NeedsAdministrator, true) };
        Assert.Equal("Services needs administrator rights.", Build(PowerModeKind.Station, single, elevated: false).Banner);
        Assert.Equal("a, b and c", FlyoutStateBuilder.JoinWithAnd(["a", "b", "c"]));
        Assert.Equal(string.Empty, FlyoutStateBuilder.JoinWithAnd([]));
    }

    [Fact]
    public void Toast_summarises_the_switch()
    {
        var station = TargetPlanner.Plan(PowerModeKind.Station, Config, RenderFixtures.Baseline());
        Assert.Equal(("Station mode", "Boost off · max 80 % · monitor 35 % @ 60 Hz · GPU 90 W · Docker stopped"), FlyoutStateBuilder.Toast(station, new Dictionary<LeverKind, LeverResult>()));

        var grid = TargetPlanner.Plan(PowerModeKind.Grid, Config, null);
        var failed = new Dictionary<LeverKind, LeverResult> { [LeverKind.Gpu] = LeverResult.Failed("x") };
        Assert.Equal(("Grid mode", "Your settings restored · 1 lever failed"), FlyoutStateBuilder.Toast(grid, failed));

        var kept = TargetPlanner.Plan(PowerModeKind.Station, Config, RenderFixtures.Baseline(), new State.ModeOptions { KeepDocker = true });
        Assert.EndsWith("Docker kept", FlyoutStateBuilder.Toast(kept, failed.Where(_ => false).ToDictionary()).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitor_and_cpu_facts_handle_missing_values()
    {
        var config = AppConfig.CreateDefault();
        config.Modes.Station.RefreshRateHz = null;
        config.Modes.Station.Scheme.Boost = true;
        config.Modes.Station.Scheme.DisplayOffSeconds = 0;
        config.Modes.Station.Scheme.SleepSeconds = 3600;
        config.Modes.Station.Scheme.HibernateSeconds = 0;
        var target = TargetPlanner.Plan(PowerModeKind.Station, config, null);

        Assert.Equal(["35 %"], FlyoutStateBuilder.Describe(LeverRow.Monitor, target));
        Assert.Equal(["Boost on · max 80 %"], FlyoutStateBuilder.Describe(LeverRow.Cpu, target));
        Assert.Equal(["Stays on · Sleep 1 h · No hibernate"], FlyoutStateBuilder.Describe(LeverRow.Display, target));
        Assert.Equal(["Stopped"], FlyoutStateBuilder.Describe(LeverRow.Docker, target));

        config.Modes.Station.BrightnessPercent = null;
        config.Modes.Station.RefreshRateHz = 60;
        config.Modes.Station.GpuLimitWatts = null;
        config.Modes.Station.CloseApps = [];
        config.Modes.Station.PauseWallpaperEngine = false;
        config.Modes.Station.StopServices = [];
        config.Modes.Station.StopDocker = false;
        var bare = TargetPlanner.Plan(PowerModeKind.Station, config, null);
        Assert.Equal(["60 Hz"], FlyoutStateBuilder.Describe(LeverRow.Monitor, bare));
        Assert.Equal(["Unchanged"], FlyoutStateBuilder.Describe(LeverRow.Gpu, bare));
        Assert.Equal(["Unchanged"], FlyoutStateBuilder.Describe(LeverRow.Apps, bare));
        Assert.Equal(["Unchanged"], FlyoutStateBuilder.Describe(LeverRow.Services, bare));
        Assert.Equal(["Unchanged"], FlyoutStateBuilder.Describe(LeverRow.Docker, bare));

        config.Modes.Station.RefreshRateHz = null;
        Assert.Equal(["Unchanged"], FlyoutStateBuilder.Describe(LeverRow.Monitor, TargetPlanner.Plan(PowerModeKind.Station, config, null)));
        Assert.Equal(["Kept running"], FlyoutStateBuilder.Describe(LeverRow.Docker, TargetPlanner.Plan(PowerModeKind.Station, config, RenderFixtures.Baseline())));
    }

    [Theory]
    [InlineData(300, "5 min")]
    [InlineData(1800, "30 min")]
    [InlineData(3600, "1 h")]
    [InlineData(5400, "1 h 30 min")]
    [InlineData(90, "90 s")]
    public void Durations_read_naturally(int seconds, string text)
    {
        Assert.Equal(text, FlyoutStateBuilder.Duration(seconds));
    }

    [Fact]
    public void Footer_compacts_shared_modifiers()
    {
        Assert.Equal("Ctrl+Alt+1 · 2 · 3 switch modes", FlyoutStateBuilder.Footer(new HotkeyConfig()));
        Assert.Equal("Ctrl+Alt+1 · Win+F2 switch modes", FlyoutStateBuilder.Footer(new HotkeyConfig { Station = "Win+F2", Reserve = "" }));
        Assert.Equal("No hotkeys", FlyoutStateBuilder.Footer(new HotkeyConfig { Grid = "", Station = "", Reserve = "x" }));
    }

    [Fact]
    public void Render_fixtures_cover_every_requested_state()
    {
        var fixtures = RenderFixtures.All();

        Assert.Equal(
            [RenderFixtures.Grid, RenderFixtures.Station, RenderFixtures.Reserve, RenderFixtures.StationApplying, RenderFixtures.StationFailed, RenderFixtures.ReserveLight],
            fixtures.Select(f => f.FileName));

        var applying = fixtures.Single(f => f.FileName == RenderFixtures.StationApplying).State;
        Assert.True(applying.IsApplying);
        Assert.Equal(RowGlyph.Applying, applying.Rows.Single(r => r.Row == LeverRow.Cpu).Glyph);
        Assert.Equal(RowGlyph.Applying, applying.Rows.Single(r => r.Row == LeverRow.Docker).Glyph);
        Assert.Equal(RowGlyph.Ok, applying.Rows.Single(r => r.Row == LeverRow.Gpu).Glyph);

        var failed = fixtures.Single(f => f.FileName == RenderFixtures.StationFailed).State;
        Assert.NotNull(failed.Banner);
        Assert.Equal(LeverResult.NeedsAdministrator, failed.Rows.Single(r => r.Row == LeverRow.Gpu).FailureReason);
        Assert.Equal(LeverResult.NeedsAdministrator, failed.Rows.Single(r => r.Row == LeverRow.Services).FailureReason);
        Assert.Equal("Station · 2 levers failed", failed.StatusLine);

        Assert.True(fixtures.Single(f => f.FileName == RenderFixtures.ReserveLight).LightTheme);
        Assert.Equal(PowerModeKind.Grid, fixtures[0].State.Mode);
        Assert.All(fixtures.Where(f => f.FileName != RenderFixtures.StationApplying), f => Assert.False(f.State.IsApplying));
    }

    [Fact]
    public void Status_text_lists_every_row_and_wraps_long_lines()
    {
        var state = Build(PowerModeKind.Reserve, results: new() { [LeverKind.Gpu] = LeverResult.Failed(LeverResult.NeedsAdministrator, true) }, elevated: false, live: new LiveValues(1.6, 14, 9, 10, 60, false, 0));

        var text = TextReports.Status(state);

        Assert.Contains("PowerMode · Reserve · 1 lever failed", text, StringComparison.Ordinal);
        Assert.Contains("GPU limit needs administrator rights.", text, StringComparison.Ordinal);
        Assert.Contains("FAILED: Needs administrator", text, StringComparison.Ordinal);
        Assert.Contains("postgresql-x64-18", text, StringComparison.Ordinal);
        Assert.Contains("engine stopped", text, StringComparison.Ordinal);
        Assert.All(text.Split(Environment.NewLine), line => Assert.True(line.Length <= 110, line));
        Assert.Equal(["a b", "c"], TextReports.Wrap("a b c", 3));

        var station = TextReports.Status(Build(PowerModeKind.Station, applying: [LeverKind.Docker], results: new() { [LeverKind.Gpu] = LeverResult.Ok("x") }));
        Assert.Contains("Keep running: off", station, StringComparison.Ordinal);
        Assert.Contains("applying", station, StringComparison.Ordinal);
        Assert.Contains(" ok", station, StringComparison.Ordinal);
    }

    [Fact]
    public void Dry_run_text_shows_each_plan_kind()
    {
        var target = TargetPlanner.Plan(PowerModeKind.Station, Config, null);
        var report = new DryRunReport(PowerModeKind.Grid, PowerModeKind.Station, true, target,
        [
            new DryRunLever(LeverKind.Gpu, LeverPlan.Of([new LeverAction("GPU 0: power limit 120 W -> 90 W", _ => Task.CompletedTask, RequiresElevation: true)]), IsElevated: false),
            new DryRunLever(LeverKind.Brightness, LeverPlan.Of([new LeverAction("brightness 80 -> 35", _ => Task.CompletedTask)]), IsElevated: false),
            new DryRunLever(LeverKind.Docker, LeverPlan.Fail("Docker is not installed"), IsElevated: false),
            new DryRunLever(LeverKind.Efficiency, LeverPlan.None, IsElevated: false),
            new DryRunLever(LeverKind.PowerScheme, LeverPlan.Done, IsElevated: false),
        ]);

        var text = TextReports.DryRun(report);

        Assert.Contains("Dry run: Grid -> Station. Nothing is changed.", text, StringComparison.Ordinal);
        Assert.Contains("baseline snapshot would be captured", text, StringComparison.Ordinal);
        Assert.Contains("  - GPU 0: power limit 120 W -> 90 W  [needs administrator: would fail]", text, StringComparison.Ordinal);
        Assert.Contains("  - brightness 80 -> 35" + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Contains("Docker: would fail - Docker is not installed", text, StringComparison.Ordinal);
        Assert.Contains("Efficiency mode: not used in this mode", text, StringComparison.Ordinal);
        Assert.Contains("Power scheme: already set", text, StringComparison.Ordinal);
    }
}
