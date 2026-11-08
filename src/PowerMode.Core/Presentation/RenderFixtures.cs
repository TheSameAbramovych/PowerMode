using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;
using PowerMode.Core.Planning;
using PowerMode.Core.State;

namespace PowerMode.Core.Presentation;

public sealed record RenderFixture(string FileName, FlyoutState State, bool LightTheme);

/// <summary>Fake but realistic flyout states for <c>--render-states</c>. Built from the default config; touches nothing.</summary>
public static class RenderFixtures
{
    public const string Grid = "grid.png";
    public const string Station = "station.png";
    public const string Reserve = "reserve.png";
    public const string StationApplying = "station-applying.png";
    public const string StationFailed = "station-failed.png";
    public const string ReserveLight = "reserve-light.png";

    /// <summary>A fixed afternoon so the renders are reproducible.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 9, 15, 5, 0, TimeSpan.FromHours(2));
    public static readonly DateTimeOffset Since = new(2026, 11, 9, 14, 32, 0, TimeSpan.FromHours(2));

    public static IReadOnlyList<RenderFixture> All(AppConfig? config = null)
    {
        config ??= AppConfig.CreateDefault();
        var snapshot = Baseline();
        var stationLive = new LiveValues(2.6, 11, 12, 35, 60, false, 0);
        var reserveLive = new LiveValues(1.6, 14, 9, 10, 60, false, 0);
        var gridLive = new LiveValues(3.9, 9, 20, 80, 199, true, 6);

        return
        [
            new(Grid, Build(config, PowerModeKind.Grid, null, AllOk(PowerModeKind.Grid), [], gridLive, isElevated: false), false),
            new(Station, Build(config, PowerModeKind.Station, snapshot, AllOk(PowerModeKind.Station), [], stationLive, isElevated: true), false),
            new(Reserve, Build(config, PowerModeKind.Reserve, snapshot, AllOk(PowerModeKind.Reserve), [], reserveLive, isElevated: true), false),
            new(StationApplying, Build(
                config,
                PowerModeKind.Station,
                snapshot,
                Without(AllOk(PowerModeKind.Station), LeverKind.PowerScheme, LeverKind.Docker),
                [LeverKind.PowerScheme, LeverKind.Docker],
                stationLive with { CpuGhz = 3.4, CpuUtilityPercent = 18, DockerEngineRunning = true, DockerContainersRunning = 6 },
                isElevated: false), false),
            new(StationFailed, Build(
                config,
                PowerModeKind.Station,
                snapshot,
                WithElevationFailures(AllOk(PowerModeKind.Station), LeverKind.Gpu, LeverKind.Services),
                [],
                stationLive with { GpuWatts = 19 },
                isElevated: false), false),
            new(ReserveLight, Build(config, PowerModeKind.Reserve, snapshot, AllOk(PowerModeKind.Reserve), [], reserveLive, isElevated: true), true),
        ];
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

    private static FlyoutState Build(
        AppConfig config,
        PowerModeKind mode,
        Snapshot? snapshot,
        Dictionary<LeverKind, LeverResult> results,
        HashSet<LeverKind> applying,
        LiveValues live,
        bool isElevated)
    {
        var target = TargetPlanner.Plan(mode, config, snapshot, new ModeOptions());
        return FlyoutStateBuilder.Build(new FlyoutInput(target, results, applying, live, Since, Now, isElevated, KeepDocker: false, StartWithWindows: true, config.Hotkeys));
    }

    private static Dictionary<LeverKind, LeverResult> AllOk(PowerModeKind mode) =>
        Enum.GetValues<LeverKind>().ToDictionary(
            k => k,
            k => k == LeverKind.Efficiency && mode == PowerModeKind.Station ? LeverResult.Skipped(LeverResult.NotOwned) : LeverResult.Ok(LeverResult.AlreadySet));

    private static Dictionary<LeverKind, LeverResult> Without(Dictionary<LeverKind, LeverResult> results, params LeverKind[] kinds)
    {
        foreach (var kind in kinds)
        {
            results.Remove(kind);
        }

        return results;
    }

    private static Dictionary<LeverKind, LeverResult> WithElevationFailures(Dictionary<LeverKind, LeverResult> results, params LeverKind[] kinds)
    {
        foreach (var kind in kinds)
        {
            results[kind] = LeverResult.Failed(LeverResult.NeedsAdministrator, needsElevation: true);
        }

        return results;
    }
}
