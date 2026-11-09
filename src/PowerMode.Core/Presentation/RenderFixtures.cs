using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;
using PowerMode.Core.Planning;
using PowerMode.Core.Power;
using PowerMode.Core.Settings;
using PowerMode.Core.State;

namespace PowerMode.Core.Presentation;

/// <summary>A flyout state to render; with <see cref="Settings"/> the Apps settings page is shown instead of the lever table, with <see cref="Feedback"/> on it; <see cref="CalibrationReading"/> shows the power card's calibration form with that text; <see cref="Levers"/> shows the Levers page for <see cref="Machine"/>.</summary>
public sealed record RenderFixture(
    string FileName,
    FlyoutState State,
    bool LightTheme,
    AppSettingsPage? Settings = null,
    SettingsFeedback? Feedback = null,
    string? CalibrationReading = null,
    IReadOnlyList<LeverSettingRow>? Levers = null,
    MachineInfo? Machine = null);

/// <summary>Fake but realistic flyout states for <c>--render-states</c>. Built from the default config; touches nothing.</summary>
public static class RenderFixtures
{
    public const string Grid = "grid.png";
    public const string GridRestored = "grid-restored.png";
    public const string Station = "station.png";
    public const string Reserve = "reserve.png";
    public const string StationApplying = "station-applying.png";
    public const string StationFailed = "station-failed.png";
    public const string ReserveFailed = "reserve-failed.png";
    public const string ReserveLight = "reserve-light.png";
    public const string SettingsApps = "settings-apps.png";
    public const string SettingsAppsLight = "settings-apps-light.png";
    public const string SettingsAppsSaved = "settings-apps-saved.png";
    public const string HeroGrid = "hero-grid.png";
    public const string HeroCalibrating = "hero-calibrating.png";
    public const string StationTwoMonitors = "station-two-monitors.png";
    public const string SettingsLevers = "settings-levers.png";
    public const string SettingsLeversLight = "settings-levers-light.png";

    /// <summary>An app with a long name, closed in Reserve, so the settings render shows how such a name wraps.</summary>
    public const string LongName = "Adobe Creative Cloud Experience";

    /// <summary>A fixed afternoon so the renders are reproducible.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 9, 15, 5, 0, TimeSpan.FromHours(2));
    public static readonly DateTimeOffset Since = new(2026, 11, 9, 14, 32, 0, TimeSpan.FromHours(2));

    /// <summary>Live values of the Grid fixtures; the power history's Grid part is their estimate.</summary>
    public static readonly LiveValues GridLive = new(3.9, 9, 20, 80, 199, true, 6);

    /// <summary>A calibration made this morning (the hero fixtures show "Calibrated 10:31").</summary>
    public static readonly PowerCalibration Calibration = new(14, new DateTimeOffset(2026, 11, 9, 10, 31, 0, TimeSpan.FromHours(2)));

    /// <summary>The owner's PC as the Levers page shows it (two monitors, the NVIDIA card's 60–132 W range).</summary>
    public static readonly MachineInfo OwnerPc = new(
        "AMD Ryzen 7 2700 Eight-Core Processor", 3200, new GpuInfo("NVIDIA GeForce RTX 3060 Ti", 60, 132), ["M27Q2", "T24i-30"], [59, 60, 75, 100, 120, 144, 165, 200], true, true, false, "1.0.0");

    public static IReadOnlyList<RenderFixture> All(AppConfig? config = null)
    {
        config ??= AppConfig.CreateDefault();
        var snapshot = Baseline();
        var stationLive = new LiveValues(2.6, 11, 12, 35, 60, false, 0);
        var reserveLive = new LiveValues(1.6, 14, 9, 10, 60, false, 0);
        var gridLive = GridLive;

        return
        [
            new(Grid, Build(config, PowerModeKind.Grid, null, _ => { }, [], gridLive, isElevated: false), false),
            new(GridRestored, Build(config, PowerModeKind.Grid, snapshot, _ => { }, [], gridLive, isElevated: true), false),
            new(Station, Build(config, PowerModeKind.Station, snapshot, _ => { }, [], stationLive, isElevated: true), false),
            new(Reserve, Build(config, PowerModeKind.Reserve, snapshot, _ => { }, [], reserveLive, isElevated: true), false),
            new(StationApplying, Build(
                config,
                PowerModeKind.Station,
                snapshot,
                r => Without(r, LeverKind.PowerScheme, LeverKind.Docker),
                [LeverKind.PowerScheme, LeverKind.Docker],
                stationLive with { CpuGhz = 3.4, CpuUtilityPercent = 18, DockerEngineRunning = true, DockerContainersRunning = 6 },
                isElevated: false), false),
            new(StationFailed, Build(
                config,
                PowerModeKind.Station,
                snapshot,
                r => WithElevationFailures(r, LeverKind.Gpu, LeverKind.Services),
                [],
                stationLive with { GpuWatts = 19 },
                isElevated: false), false),
            new(ReserveFailed, Build(
                config,
                PowerModeKind.Reserve,
                snapshot,
                r => WithElevationFailures(r, LeverKind.Gpu, LeverKind.Services),
                [],
                reserveLive with { GpuWatts = 19 },
                isElevated: false), false),
            new(ReserveLight, Build(config, PowerModeKind.Reserve, snapshot, _ => { }, [], reserveLive, isElevated: true), true),
            new(SettingsApps, Build(config, PowerModeKind.Station, snapshot, _ => { }, [], stationLive, isElevated: true), false, SettingsPage(config)),
            new(SettingsAppsLight, Build(config, PowerModeKind.Station, snapshot, _ => { }, [], stationLive, isElevated: true), true, SettingsPage(config)),
            new(SettingsAppsSaved, Build(config, PowerModeKind.Station, snapshot, _ => { }, [], stationLive, isElevated: true), false, SettingsPage(config), SettingsFeedback.SavedAt(Now)),
            new(HeroGrid, Build(config, PowerModeKind.Grid, null, _ => { }, [], gridLive, isElevated: true, Calibration), false),
            new(HeroCalibrating, Build(config, PowerModeKind.Station, snapshot, _ => { }, [], stationLive, isElevated: true, Calibration), false, CalibrationReading: "186"),
            new(SettingsLevers, Build(config, PowerModeKind.Station, snapshot, _ => { }, [], stationLive, isElevated: true), false, Feedback: SettingsFeedback.SavedAt(Now), Levers: LeverSettings.Build(config, OwnerPc), Machine: OwnerPc),
            new(SettingsLeversLight, Build(config, PowerModeKind.Station, snapshot, _ => { }, [], stationLive, isElevated: true), true, Levers: LeverSettings.Build(config, OwnerPc), Machine: OwnerPc),
            new(StationTwoMonitors, Build(config, PowerModeKind.Station, snapshot, _ => { }, [], stationLive with { Monitors = [new MonitorLive("M27Q2", 35, 59), new MonitorLive("T24i-30", 35, 60)] }, isElevated: true), false),
        ];
    }

    /// <summary>
    /// The Apps settings page over the given config, with Telegram set to efficiency mode in Station too and a few
    /// running apps, one with a long name that Reserve closes. The config itself is not changed.
    /// </summary>
    public static AppSettingsPage SettingsPage(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var copy = ConfigLoader.Parse(ConfigLoader.Serialize(config)).Config!;
        AppSettings.SetChoice(copy.Modes.Station, "Telegram", AppChoice.Efficiency);
        AppSettings.SetChoice(copy.Modes.Reserve, LongName, AppChoice.Close);
        RunningApp[] running =
        [
            new("discord", HasWindow: true, IsSystem: false),
            new(LongName, HasWindow: true, IsSystem: false),
            new("Code", HasWindow: true, IsSystem: false),
            new("explorer", HasWindow: true, IsSystem: true),
        ];
        return AppSettings.Build(copy, running);
    }

    /// <summary>The owner's PC as measured on 2026-11-09.</summary>
    public static Snapshot Baseline() => new()
    {
        CapturedAt = Since,
        ActiveScheme = Guid.Parse("9897998c-92de-4669-853f-b7cd3ecb2790"),
        Monitors = [new MonitorLevel(@"\\.\DISPLAY1#0", 80, 50)],
        Displays = [new DisplayModeInfo(@"\\.\DISPLAY1", 2560, 1440, 199, 32)],
        Gpu = new GpuSnapshot(120_000, 120_000),
        WallpaperEngineRunning = true,
        Docker = new DockerSnapshot(true, ["p365-kafka", "p365-zookeeper", "p365-redis", "qm-sonar", "qm-sonar-db", "gitlab-runner"]),
        Processes = [new CapturedProcess("EPSDNMON", @"C:\Program Files\Epson\EPSDNMON.exe", string.Empty)],
        Services = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["WSearch"] = true,
            ["SysMain"] = true,
            ["DiagTrack"] = true,
            ["Spooler"] = true,
            ["MongoDB"] = true,
            ["MySQL80"] = true,
            ["postgresql-x64-18"] = true,
        },
    };

    /// <summary>
    /// What a successful apply of this target reports: "not used in this mode" where the target leaves a lever alone
    /// (as each lever plans it), "already set" everywhere else.
    /// </summary>
    public static Dictionary<LeverKind, LeverResult> ResultsFor(TargetState target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Enum.GetValues<LeverKind>().ToDictionary(
            k => k,
            k => target.Sets(k) ? LeverResult.Ok(LeverResult.AlreadySet) : LeverResult.Skipped(LeverResult.NotOwned));
    }

    private static FlyoutState Build(
        AppConfig config,
        PowerModeKind mode,
        Snapshot? snapshot,
        Action<Dictionary<LeverKind, LeverResult>> adjust,
        HashSet<LeverKind> applying,
        LiveValues live,
        bool isElevated,
        PowerCalibration? calibration = null)
    {
        var target = TargetPlanner.Plan(mode, config, snapshot, new ModeOptions());
        var results = ResultsFor(target);
        adjust(results);
        return FlyoutStateBuilder.Build(new FlyoutInput(
            target, results, applying, live, Since, Now, isElevated, KeepDocker: false, StartWithWindows: true, config.Hotkeys, PowerFor(config, mode, live, calibration)));
    }

    /// <summary>
    /// The power card of a fixture: the estimate of its live values, and 10 minutes of history in which a saving mode
    /// started 4 minutes ago after 6 minutes of Grid (at the Grid fixture's estimate), with a gentle wobble.
    /// </summary>
    public static PowerCard PowerFor(AppConfig config, PowerModeKind mode, LiveValues live, PowerCalibration? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(live);
        var estimate = PowerEstimator.Estimate(config.Power, live.ToPowerInputs(), calibration);
        var grid = PowerEstimator.Estimate(config.Power, GridLive.ToPowerInputs(), calibration).TotalW;
        DateTimeOffset? switchedAt = mode == PowerModeKind.Grid ? null : Now.AddMinutes(-4);
        var history = new PowerHistory();
        for (var second = -598; second <= 0; second += 2)
        {
            var at = Now.AddSeconds(second);
            var level = at >= switchedAt ? estimate.TotalW : grid;
            history.Add(at, level + 2.5 * Math.Sin(second / 23.0) + 1.5 * Math.Sin(second / 7.0));
        }

        if (switchedAt is { } switched)
        {
            history.MarkSwitch(switched);
        }

        return PowerCards.Build(estimate, calibration, history.DeltaVsGrid(mode, grid, switchedAt, Now), history.Points, history.SwitchedAt, Now);
    }

    private static void Without(Dictionary<LeverKind, LeverResult> results, params LeverKind[] kinds)
    {
        foreach (var kind in kinds)
        {
            results.Remove(kind);
        }
    }

    private static void WithElevationFailures(Dictionary<LeverKind, LeverResult> results, params LeverKind[] kinds)
    {
        foreach (var kind in kinds)
        {
            results[kind] = LeverResult.Failed(LeverResult.NeedsAdministrator, needsElevation: true);
        }
    }
}
