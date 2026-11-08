using PowerMode.Core.Config;
using PowerMode.Core.Model;
using PowerMode.Core.Planning;
using PowerMode.Core.Presentation;
using PowerMode.Core.State;

namespace PowerMode.Core.Tests;

public sealed class TargetPlannerTests
{
    private static readonly AppConfig Config = AppConfig.CreateDefault();
    private static readonly Snapshot Baseline = RenderFixtures.Baseline();

    [Fact]
    public void Station_sets_every_lever_from_the_spec_defaults()
    {
        var target = TargetPlanner.Plan(PowerModeKind.Station, Config, Baseline);

        Assert.Equal(PowerModeKind.Station, target.Mode);
        Assert.Equal(PowerSchemes.Station, target.Scheme!.Scheme);
        var scheme = target.Scheme.Configure!;
        Assert.False(scheme.Boost);
        Assert.Equal(80, scheme.MaxProcessorPercent);
        Assert.Equal(5, scheme.MinProcessorPercent);
        Assert.Equal(300, scheme.DisplayOffSeconds);
        Assert.Equal(0, scheme.SleepSeconds);
        Assert.Equal(1800, scheme.HibernateSeconds);
        Assert.Equal(PowerSchemes.Balanced, scheme.SourceGuid);
        Assert.Equal(35, target.Monitor!.BrightnessPercent);
        Assert.Equal(60, target.Refresh!.RefreshHz);
        Assert.Equal(90, target.Gpu!.LimitWatts);
        Assert.Null(target.Gpu.RestoreMilliwatts);
        Assert.Equal(WallpaperAction.Pause, target.Wallpaper);
        Assert.False(target.Docker!.Running);
        Assert.Equal(6, target.Docker.SnapshotContainerCount);
        Assert.Equal(["steam", "EPSDNMON"], target.Apps.Close);
        Assert.Empty(target.Efficiency.ProcessNames);
        Assert.Equal(["WSearch"], target.Services.Stop);
    }

    [Fact]
    public void Station_keeps_what_it_does_not_reduce_as_in_the_snapshot()
    {
        var target = TargetPlanner.Plan(PowerModeKind.Station, Config, Baseline);

        // Services the snapshot had running and Station does not stop are started again (e.g. after Reserve).
        Assert.Equal(["DiagTrack", "MongoDB", "MySQL80", "postgresql-x64-18", "Spooler", "SysMain"], target.Services.Start);
        // EPSDNMON is closed by Station, so it is not in the relaunch list.
        Assert.DoesNotContain(target.Apps.Relaunch, p => p.Name == "EPSDNMON");
    }

    [Fact]
    public void Reserve_reduces_more_than_station()
    {
        var target = TargetPlanner.Plan(PowerModeKind.Reserve, Config, Baseline);

        Assert.Equal(PowerSchemes.Reserve, target.Scheme!.Scheme);
        Assert.Equal(PowerSchemes.PowerSaver, target.Scheme.Configure!.SourceGuid);
        Assert.Equal(50, target.Scheme.Configure.MaxProcessorPercent);
        Assert.Equal(120, target.Scheme.Configure.DisplayOffSeconds);
        Assert.Equal(600, target.Scheme.Configure.HibernateSeconds);
        Assert.Equal(10, target.Monitor!.BrightnessPercent);
        Assert.Equal(60, target.Gpu!.LimitWatts);
        Assert.Contains("ChatGPT", target.Apps.Close);
        Assert.Contains("Postman Agent", target.Apps.Close);
        Assert.Equal(["Slack", "Telegram", "msedgewebview2", "PhoneExperienceHost", "CrossDeviceService"], target.Efficiency.ProcessNames);
        Assert.Equal(7, target.Services.Stop.Count);
        Assert.Empty(target.Services.Start);
        Assert.False(target.Docker!.Running);
    }

    [Fact]
    public void Grid_restores_the_snapshot_lever_by_lever()
    {
        var target = TargetPlanner.Plan(PowerModeKind.Grid, Config, Baseline);

        Assert.Equal(Baseline.ActiveScheme, target.Scheme!.Scheme);
        Assert.Null(target.Scheme.Configure);
        Assert.Null(target.Monitor!.BrightnessPercent);
        Assert.Equal(80, target.Monitor.Restore.Single().Brightness);
        Assert.Equal(199, target.Refresh!.Restore.Single().RefreshHz);
        Assert.Equal(120_000u, target.Gpu!.RestoreMilliwatts);
        Assert.Equal(WallpaperAction.Play, target.Wallpaper);
        Assert.True(target.Docker!.Running);
        Assert.Equal(6, target.Docker.ContainersToStart.Count);
        Assert.Empty(target.Apps.Close);
        Assert.Equal(Baseline.Processes, target.Apps.Relaunch);
        Assert.Empty(target.Efficiency.ProcessNames);
        Assert.Empty(target.Services.Stop);
        Assert.Equal(7, target.Services.Start.Count);
    }

    [Fact]
    public void Grid_without_snapshot_leaves_everything_and_clears_efficiency()
    {
        var target = TargetPlanner.Plan(PowerModeKind.Grid, Config, null);

        Assert.Null(target.Scheme);
        Assert.Null(target.Monitor);
        Assert.Null(target.Refresh);
        Assert.Null(target.Gpu);
        Assert.Null(target.Docker);
        Assert.Equal(WallpaperAction.Leave, target.Wallpaper);
        Assert.Empty(target.Apps.Relaunch);
        Assert.Empty(target.Efficiency.ProcessNames);
    }

    [Fact]
    public void Grid_never_restores_one_of_its_own_schemes()
    {
        var snapshot = RenderFixtures.Baseline();
        snapshot.ActiveScheme = PowerSchemes.Station;

        Assert.Null(TargetPlanner.Plan(PowerModeKind.Grid, Config, snapshot).Scheme);
    }

    [Fact]
    public void Grid_with_partial_snapshot_leaves_missing_parts()
    {
        var snapshot = new Snapshot { Docker = new DockerSnapshot(false, []) };

        var target = TargetPlanner.Plan(PowerModeKind.Grid, Config, snapshot);

        Assert.Null(target.Scheme);
        Assert.Null(target.Monitor);
        Assert.Null(target.Refresh);
        Assert.Null(target.Gpu);
        Assert.Null(target.Docker);
        Assert.Equal(WallpaperAction.Leave, target.Wallpaper);
    }

    [Fact]
    public void Keep_docker_runs_the_snapshot_containers_minus_exclusions()
    {
        var config = AppConfig.CreateDefault();
        config.Docker.ExcludedContainers = ["QM-SONAR"];

        var target = TargetPlanner.Plan(PowerModeKind.Station, config, Baseline, new ModeOptions { KeepDocker = true });

        Assert.True(target.Docker!.Running);
        Assert.Equal(5, target.Docker.ContainersToStart.Count);
        Assert.DoesNotContain("qm-sonar", target.Docker.ContainersToStart);
    }

    [Fact]
    public void Keep_docker_without_running_engine_in_snapshot_leaves_docker()
    {
        var snapshot = RenderFixtures.Baseline();
        snapshot.Docker = new DockerSnapshot(false, []);

        Assert.Null(TargetPlanner.Plan(PowerModeKind.Station, Config, snapshot, new ModeOptions { KeepDocker = true }).Docker);
        Assert.Null(TargetPlanner.Plan(PowerModeKind.Station, Config, null, new ModeOptions { KeepDocker = true }).Docker);
    }

    [Fact]
    public void Saving_mode_without_snapshot_has_nothing_to_restore()
    {
        var target = TargetPlanner.Plan(PowerModeKind.Station, Config, null);

        Assert.Empty(target.Apps.Relaunch);
        Assert.Empty(target.Services.Start);
        Assert.Equal(0, target.Docker!.SnapshotContainerCount);
    }

    [Fact]
    public void Null_config_values_leave_those_levers_alone()
    {
        var config = AppConfig.CreateDefault();
        config.Modes.Station.BrightnessPercent = null;
        config.Modes.Station.RefreshRateHz = null;
        config.Modes.Station.GpuLimitWatts = null;
        config.Modes.Station.PauseWallpaperEngine = false;

        var target = TargetPlanner.Plan(PowerModeKind.Station, config, Baseline);

        Assert.Null(target.Monitor);
        Assert.Null(target.Refresh);
        Assert.Null(target.Gpu);
        Assert.Equal(WallpaperAction.Play, target.Wallpaper);
        Assert.Equal(WallpaperAction.Leave, TargetPlanner.Plan(PowerModeKind.Station, config, null).Wallpaper);
    }
}
