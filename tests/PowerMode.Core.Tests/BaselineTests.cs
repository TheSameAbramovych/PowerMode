using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;
using PowerMode.Core.State;
using PowerMode.Core.Switching;

namespace PowerMode.Core.Tests;

/// <summary>The baseline is never wrong: failed captures, interrupted switches, failed or partial Grid restores, lost state.</summary>
public sealed class BaselineTests
{
    [Fact]
    public async Task A_failed_capture_blocks_the_reduction_and_grid_leaves_the_lever_alone()
    {
        var monitor = new BrightnessMachine { FailCaptures = 2 };
        var lever = monitor.Lever();
        using var harness = new SwitcherHarness([lever]);

        var station = await harness.Switcher.SwitchAsync(PowerModeKind.Station);

        Assert.Equal(2, lever.Captures);
        Assert.Equal(LeverStatus.Failed, station.Results[LeverKind.Brightness].Status);
        Assert.Equal(LeverResult.BaselineNotCaptured, station.Results[LeverKind.Brightness].Detail);
        Assert.Equal(80, monitor.Value);
        Assert.Empty(lever.Planned);
        Assert.Contains(LeverKind.Brightness, harness.Reload().Snapshot!.Missing);

        var grid = await harness.Switcher.SwitchAsync(PowerModeKind.Grid);

        Assert.Equal(0, grid.FailedCount);
        Assert.Equal(LeverStatus.Skipped, grid.Results[LeverKind.Brightness].Status);
        Assert.Equal(80, monitor.Value);
        Assert.Null(harness.Switcher.State.Snapshot);
    }

    [Fact]
    public async Task A_capture_that_fails_once_is_retried_before_reducing()
    {
        var monitor = new BrightnessMachine { FailCaptures = 1 };
        using var harness = new SwitcherHarness([monitor.Lever()]);

        var station = await harness.Switcher.SwitchAsync(PowerModeKind.Station);

        Assert.Equal(0, station.FailedCount);
        Assert.Equal(35, monitor.Value);
        Assert.Empty(harness.Switcher.State.Snapshot!.Missing);
        Assert.Equal(80, harness.Reload().Snapshot!.Monitors.Single().Brightness);

        await harness.Switcher.SwitchAsync(PowerModeKind.Grid);
        Assert.Equal(80, monitor.Value);
    }

    [Fact]
    public async Task A_missing_part_is_captured_by_a_later_saving_switch()
    {
        var monitor = new BrightnessMachine { FailCaptures = 2 };
        using var harness = new SwitcherHarness([monitor.Lever()]);
        await harness.Switcher.SwitchAsync(PowerModeKind.Station);
        Assert.Equal(80, monitor.Value);

        var reserve = await harness.Switcher.SwitchAsync(PowerModeKind.Reserve);

        Assert.Equal(0, reserve.FailedCount);
        Assert.Equal(10, monitor.Value);
        Assert.Equal(80, harness.Reload().Snapshot!.Monitors.Single().Brightness);
        await harness.Switcher.SwitchAsync(PowerModeKind.Grid);
        Assert.Equal(80, monitor.Value);
    }

    [Fact]
    public async Task A_failed_scheme_capture_still_applies_the_scheme_and_grid_activates_the_fallback()
    {
        var scheme = new SchemeMachine { FailCaptures = true };
        using var harness = new SwitcherHarness([scheme.Lever()]);

        var station = await harness.Switcher.SwitchAsync(PowerModeKind.Station);

        Assert.Equal(0, station.FailedCount);
        Assert.Equal(PowerSchemes.Station, scheme.Active);
        Assert.Contains(LeverKind.PowerScheme, harness.Switcher.State.Snapshot!.Missing);

        var grid = await harness.Switcher.SwitchAsync(PowerModeKind.Grid);

        Assert.Equal(0, grid.FailedCount);
        Assert.Equal(harness.Config.FallbackScheme, scheme.Active);
        Assert.Null(harness.Switcher.State.Snapshot);
    }

    [Fact]
    public async Task A_powermode_scheme_is_never_recorded_as_the_baseline()
    {
        var scheme = new SchemeMachine { Active = PowerSchemes.Reserve };
        using var harness = new SwitcherHarness([scheme.Lever()]);

        await harness.Switcher.SwitchAsync(PowerModeKind.Station);

        var snapshot = harness.Switcher.State.Snapshot!;
        Assert.Null(snapshot.ActiveScheme);
        Assert.Contains(LeverKind.PowerScheme, snapshot.Missing);
        Assert.Contains(harness.Log.Lines, l => l.Contains("not recorded as the baseline", StringComparison.Ordinal));
        await harness.Switcher.SwitchAsync(PowerModeKind.Grid);
        Assert.Equal(harness.Config.FallbackScheme, scheme.Active);
    }

    [Fact]
    public async Task An_interrupted_switch_keeps_the_old_baseline_when_grid_is_left_again()
    {
        var monitor = new BrightnessMachine { Value = 35 };
        using var harness = new SwitcherHarness([monitor.Lever()]);

        // The process died after the snapshot was saved and the levers ran: Mode is still Grid, the machine is reduced.
        harness.Store.Save(new PersistedState
        {
            Snapshot = new Snapshot { CapturedAt = harness.Time.Now, Monitors = [new MonitorLevel("m#0", 100, 50)] },
            LastResults = { [LeverKind.Brightness] = LeverResult.Ok(LeverResult.AlreadySet) },
        });
        using var switcher = new ModeSwitcher([monitor.Lever()], harness.Store, () => harness.Config, () => true, harness.Time, harness.Log);
        Assert.True(switcher.NeedsReapplyOnStart);

        await switcher.SwitchAsync(PowerModeKind.Station);
        Assert.Equal(100, switcher.State.Snapshot!.Monitors.Single().Brightness);
        await switcher.SwitchAsync(PowerModeKind.Grid);
        Assert.Equal(100, monitor.Value);
    }

    [Fact]
    public async Task Dry_run_after_an_interrupted_switch_restores_the_old_baseline()
    {
        var monitor = new BrightnessMachine { Value = 35 };
        using var harness = new SwitcherHarness([monitor.Lever()]);
        harness.Store.Save(new PersistedState
        {
            Snapshot = new Snapshot { CapturedAt = harness.Time.Now, Monitors = [new MonitorLevel("m#0", 100, 50)] },
        });
        using var switcher = new ModeSwitcher([monitor.Lever()], harness.Store, () => harness.Config, () => true, harness.Time, harness.Log);

        var report = await switcher.DryRunAsync(PowerModeKind.Grid);

        Assert.Equal(100, report.Target.Monitor!.Restore.Single().Brightness);
        Assert.Equal(35, monitor.Value);
    }

    [Fact]
    public async Task Leaving_grid_after_a_partial_restore_keeps_only_the_failed_part()
    {
        var monitor = new BrightnessMachine();
        var gpu = new GpuMachine();
        using var harness = new SwitcherHarness([monitor.Lever(), gpu.Lever()]);
        await harness.Switcher.SwitchAsync(PowerModeKind.Station);
        monitor.FailWrites = true;
        await harness.Switcher.SwitchAsync(PowerModeKind.Grid);
        Assert.Equal(120_000u, gpu.LimitMw);
        Assert.Equal([LeverKind.Gpu], harness.Switcher.State.Snapshot!.Restored);

        // The owner changes the GPU limit in Grid; that is the new baseline for the GPU, not the old one.
        gpu.LimitMw = 100_000;
        monitor.FailWrites = false;
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await harness.Switcher.SwitchAsync(PowerModeKind.Station);

        var snapshot = harness.Switcher.State.Snapshot!;
        Assert.Equal(80, snapshot.Monitors.Single().Brightness);
        Assert.Equal(100_000u, snapshot.Gpu!.LimitMilliwatts);
        Assert.Empty(snapshot.Restored);
    }

    [Fact]
    public async Task Retrying_grid_touches_only_what_is_still_owed_and_then_clears()
    {
        var monitor = new BrightnessMachine();
        var gpu = new GpuMachine();
        var gpuLever = gpu.Lever();
        using var harness = new SwitcherHarness([monitor.Lever(), gpuLever]);
        await harness.Switcher.SwitchAsync(PowerModeKind.Station);
        harness.Switcher.State.ClosedApps.Add("steam");
        monitor.FailWrites = true;
        await harness.Switcher.SwitchAsync(PowerModeKind.Grid);
        Assert.True(harness.Switcher.NeedsReapplyOnStart);

        gpu.LimitMw = 100_000;
        monitor.FailWrites = false;
        var retry = await harness.Switcher.SwitchAsync(PowerModeKind.Grid);

        Assert.Equal(0, retry.FailedCount);
        Assert.Equal(80, monitor.Value);
        Assert.Equal(100_000u, gpu.LimitMw);
        Assert.Null(gpuLever.Planned[^1].Gpu);
        Assert.Null(harness.Reload().Snapshot);
        Assert.Empty(harness.Reload().ClosedApps);
        Assert.False(harness.Switcher.NeedsReapplyOnStart);
    }

    [Fact]
    public async Task A_permanent_grid_failure_is_not_retried_forever()
    {
        var docker = new FakeLever(LeverKind.Docker)
        {
            OnCapture = s => s.Docker = new DockerSnapshot(true, ["a"]),
            OnPlan = c => c.Target.Mode == PowerModeKind.Grid ? LeverPlan.Fail("Docker is not installed", permanent: true) : LeverPlan.Done,
        };
        using var harness = new SwitcherHarness([docker]);
        await harness.Switcher.SwitchAsync(PowerModeKind.Station);

        var grid = await harness.Switcher.SwitchAsync(PowerModeKind.Grid);

        Assert.Equal(1, grid.FailedCount);
        Assert.True(grid.Results[LeverKind.Docker].Permanent);
        Assert.Null(harness.Switcher.State.Snapshot);
        Assert.False(harness.Switcher.NeedsReapplyOnStart);
    }

    [Fact]
    public async Task Names_added_to_a_close_list_in_a_saving_mode_are_captured_and_restored()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "steam" };
        using var harness = new SwitcherHarness([]);
        var apps = AppsLever(harness, running);
        using var switcher = new ModeSwitcher([apps], harness.Store, () => harness.Config, () => true, harness.Time, harness.Log);

        await switcher.SwitchAsync(PowerModeKind.Station);
        Assert.Equal(1, apps.Captures);
        Assert.Equal(["steam"], switcher.State.Snapshot!.Processes.Select(p => p.Name));

        // In Station the owner adds Discord to Reserve's list, and starts EPSDNMON (already covered, not running at capture).
        harness.Config.Modes.Reserve.CloseApps.Add("Discord");
        running.Add("Discord");
        running.Add("EPSDNMON");
        await switcher.SwitchAsync(PowerModeKind.Reserve);

        Assert.Equal(2, apps.Captures);
        Assert.Equal(["steam", "Discord"], switcher.State.Snapshot!.Processes.Select(p => p.Name));
        Assert.Equal(["steam", "Discord"], harness.Reload().Snapshot!.Processes.Select(p => p.Name));
        await switcher.SwitchAsync(PowerModeKind.Reserve);
        Assert.Equal(2, apps.Captures);

        await switcher.SwitchAsync(PowerModeKind.Grid);
        Assert.Contains(apps.Planned[^1].Apps.Relaunch, p => p.Name == "Discord");
    }

    [Fact]
    public async Task Dry_run_captures_newly_listed_names_on_a_copy_only()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "steam" };
        using var harness = new SwitcherHarness([]);
        var apps = AppsLever(harness, running);
        using var switcher = new ModeSwitcher([apps], harness.Store, () => harness.Config, () => true, harness.Time, harness.Log);
        await switcher.SwitchAsync(PowerModeKind.Station);
        harness.Config.Modes.Reserve.CloseApps.Add("Discord");
        running.Add("Discord");

        var report = await switcher.DryRunAsync(PowerModeKind.Station);

        Assert.Contains(report.Target.Apps.Relaunch, p => p.Name == "Discord");
        Assert.Equal(["steam"], switcher.State.Snapshot!.Processes.Select(p => p.Name));
        Assert.Equal(["steam"], harness.Reload().Snapshot!.Processes.Select(p => p.Name));
    }

    [Fact]
    public async Task Dry_run_reports_a_lever_without_baseline_as_not_reducing()
    {
        var monitor = new BrightnessMachine { FailCaptures = 10 };
        using var harness = new SwitcherHarness([monitor.Lever()]);

        var report = await harness.Switcher.DryRunAsync(PowerModeKind.Station);

        Assert.Equal(LeverResult.BaselineNotCaptured, report.Levers.Single().Plan.Unavailable);
    }

    [Fact]
    public async Task A_lever_without_baseline_never_counts_as_drifted()
    {
        var monitor = new BrightnessMachine { FailCaptures = 10 };
        using var harness = new SwitcherHarness([monitor.Lever()]);
        await harness.Switcher.SwitchAsync(PowerModeKind.Station);

        Assert.False(await harness.Switcher.HasDriftedAsync([LeverKind.Brightness]));
    }

    [Fact]
    public async Task Keep_docker_changed_during_a_switch_is_applied_right_after_it()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engineRunning = true;
        var docker = new FakeLever(LeverKind.Docker)
        {
            OnCapture = s => s.Docker = new DockerSnapshot(true, ["gitlab-runner"]),
            OnPlan = c => c.Target.Docker is not { } target || target.Running == engineRunning
                ? LeverPlan.Done
                : LeverPlan.Of([new LeverAction("docker", async _ =>
                {
                    await release.Task;
                    engineRunning = target.Running;
                })]),
        };
        using var harness = new SwitcherHarness([docker]);

        var switching = Task.Run(() => harness.Switcher.SwitchAsync(PowerModeKind.Station));
        await WaitUntilAsync(() => docker.Planned.Count > 0);
        var toggle = harness.Switcher.SetKeepDockerAsync(PowerModeKind.Station, true);

        Assert.True(harness.Switcher.State.OptionsFor(PowerModeKind.Station).KeepDocker);
        Assert.False(toggle.IsCompleted);
        release.SetResult();
        await Task.WhenAll(switching, toggle);

        Assert.True(engineRunning);
        Assert.True(docker.Planned[^1].Docker!.Running);
        Assert.True(harness.Reload().OptionsFor(PowerModeKind.Station).KeepDocker);
    }

    [Fact]
    public async Task Keep_docker_for_another_mode_is_only_saved()
    {
        var docker = new FakeLever(LeverKind.Docker);
        using var harness = new SwitcherHarness([docker]);

        await harness.Switcher.SetKeepDockerAsync(PowerModeKind.Station, true);

        Assert.Empty(docker.Planned);
        Assert.True(harness.Reload().OptionsFor(PowerModeKind.Station).KeepDocker);
    }

    [Fact]
    public async Task A_lost_state_while_a_powermode_scheme_is_active_adopts_that_mode()
    {
        var scheme = new SchemeMachine { Active = PowerSchemes.Reserve };
        var monitor = new BrightnessMachine { Value = 10 };
        var monitorLever = monitor.Lever();
        using var harness = new SwitcherHarness([scheme.Lever(), monitorLever]);

        Assert.True(await harness.Switcher.RecoverLostModeAsync(PowerSchemes.Reserve));

        Assert.Equal(PowerModeKind.Reserve, harness.Reload().Mode);
        Assert.True(harness.Switcher.NeedsReapplyOnStart);
        Assert.False(await harness.Switcher.RecoverLostModeAsync(PowerSchemes.Reserve));

        await harness.Switcher.SwitchAsync(PowerModeKind.Station);
        Assert.Equal(0, monitorLever.Captures);
        Assert.Equal(35, monitor.Value);

        var grid = await harness.Switcher.SwitchAsync(PowerModeKind.Grid);
        Assert.Equal(0, grid.FailedCount);
        Assert.Equal(harness.Config.FallbackScheme, scheme.Active);
        Assert.Equal(35, monitor.Value);
        Assert.Null(harness.Switcher.State.Snapshot);
    }

    [Theory]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e")]
    [InlineData(null)]
    public async Task A_lost_state_with_a_user_scheme_stays_grid(string? active)
    {
        using var harness = new SwitcherHarness([]);

        Assert.False(await harness.Switcher.RecoverLostModeAsync(active is null ? null : Guid.Parse(active)));
        Assert.Equal(PowerModeKind.Grid, harness.Switcher.State.Mode);
        Assert.False(File.Exists(harness.Store.Path));
    }

    [Fact]
    public async Task A_kept_baseline_is_not_replaced_by_mode_recovery()
    {
        using var harness = new SwitcherHarness([new BrightnessMachine().Lever()]);
        await harness.Switcher.SwitchAsync(PowerModeKind.Station);

        Assert.False(await harness.Switcher.RecoverLostModeAsync(PowerSchemes.Station));
        harness.Switcher.State.Mode = PowerModeKind.Grid;
        Assert.False(await harness.Switcher.RecoverLostModeAsync(PowerSchemes.Station));
    }

    [Fact]
    public async Task A_corrupt_state_file_in_a_saving_mode_keeps_the_baseline()
    {
        var monitor = new BrightnessMachine();
        using var harness = new SwitcherHarness([monitor.Lever()]);
        await harness.Switcher.SwitchAsync(PowerModeKind.Station);
        await File.WriteAllTextAsync(harness.Store.Path, "{\"mode\":\"station\",\"snaps");

        using var after = new ModeSwitcher([monitor.Lever()], harness.Store, () => harness.Config, () => true, harness.Time, harness.Log);

        Assert.Equal(80, after.State.Snapshot!.Monitors.Single().Brightness);
        Assert.True(after.NeedsReapplyOnStart);
        Assert.Single(Directory.GetFiles(harness.Dir.Path, "state.json.corrupt-*"));
        await after.SwitchAsync(PowerModeKind.Grid);
        Assert.Equal(80, monitor.Value);
    }

    /// <summary>Like the real process lever: captures only names its baseline has not looked at yet, and appends.</summary>
    private static FakeLever AppsLever(SwitcherHarness harness, HashSet<string> running) => new(LeverKind.Apps)
    {
        OnCapture = s =>
        {
            var covered = s.CoveredAppNames();
            var names = harness.Config.AllCloseApps().Where(n => !covered.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            s.Processes = [.. s.Processes, .. names.Where(running.Contains).Select(n => new CapturedProcess(n, n + ".exe", string.Empty))];
            s.CoveredApps = [.. covered, .. names];
        },
        OnPlan = c => c.Target.Sets(LeverKind.Apps) ? LeverPlan.Done : LeverPlan.None,
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
