using PowerMode.Core.Levers;
using PowerMode.Core.Model;
using PowerMode.Core.Switching;

namespace PowerMode.Core.Tests;

public sealed class ModeSwitcherTests
{
    [Fact]
    public async Task Grid_to_station_captures_and_persists_the_snapshot_then_applies()
    {
        var monitor = new BrightnessMachine();
        var lever = monitor.Lever();
        using var harness = new SwitcherHarness([lever]);

        var outcome = await harness.Switcher.SwitchAsync(PowerModeKind.Station);

        Assert.Equal(1, lever.Captures);
        Assert.Equal(35, monitor.Value);
        Assert.Equal(0, outcome.FailedCount);
        var saved = harness.Reload();
        Assert.Equal(PowerModeKind.Station, saved.Mode);
        Assert.Equal(80, saved.Snapshot!.Monitors.Single().Brightness);
        Assert.Equal(harness.Time.Now, saved.Since);
        Assert.Equal(LeverStatus.Ok, saved.LastResults[LeverKind.Brightness].Status);
        Assert.Contains(harness.Log.Lines, l => l.Contains("Baseline snapshot saved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Station_to_reserve_does_not_resnapshot_and_grid_restores_and_clears()
    {
        var monitor = new BrightnessMachine();
        var lever = monitor.Lever();
        using var harness = new SwitcherHarness([lever]);

        await harness.Switcher.SwitchAsync(PowerModeKind.Station);
        await harness.Switcher.SwitchAsync(PowerModeKind.Reserve);
        Assert.Equal(1, lever.Captures);
        Assert.Equal(10, monitor.Value);
        Assert.Equal(80, harness.Reload().Snapshot!.Monitors.Single().Brightness);

        await harness.Switcher.SwitchAsync(PowerModeKind.Grid);
        Assert.Equal(80, monitor.Value);
        var saved = harness.Reload();
        Assert.Equal(PowerModeKind.Grid, saved.Mode);
        Assert.Null(saved.Snapshot);
        Assert.False(harness.Switcher.NeedsReapplyOnStart);
    }

    [Fact]
    public async Task Reapplying_the_same_mode_is_idempotent()
    {
        var monitor = new BrightnessMachine();
        var lever = monitor.Lever();
        using var harness = new SwitcherHarness([lever]);
        await harness.Switcher.SwitchAsync(PowerModeKind.Station);
        var since = harness.Switcher.State.Since;
        harness.Time.Advance(TimeSpan.FromMinutes(5));

        var outcome = await harness.Switcher.SwitchAsync(PowerModeKind.Station);

        Assert.Equal(1, monitor.Writes);
        Assert.Equal(1, lever.Captures);
        Assert.Equal(LeverResult.AlreadySet, outcome.Results[LeverKind.Brightness].Detail);
        Assert.Equal(since, harness.Switcher.State.Since);
        Assert.True(harness.Switcher.NeedsReapplyOnStart);
    }

    [Fact]
    public async Task A_new_switcher_resumes_the_persisted_mode_and_snapshot()
    {
        var monitor = new BrightnessMachine();
        using var harness = new SwitcherHarness([monitor.Lever()]);
        await harness.Switcher.SwitchAsync(PowerModeKind.Reserve);

        // Simulate a reboot: the monitor comes back at full brightness, the state file survives.
        monitor.Value = 100;
        using var after = new Switching.ModeSwitcher([monitor.Lever()], harness.Store, () => harness.Config, () => true, harness.Time, harness.Log);
        Assert.True(after.NeedsReapplyOnStart);
        await after.SwitchAsync(after.State.Mode);
        Assert.Equal(10, monitor.Value);

        await after.SwitchAsync(PowerModeKind.Grid);
        Assert.Equal(80, monitor.Value);
    }

    [Fact]
    public async Task Partial_failure_still_applies_the_mode_and_reports_elevation()
    {
        var monitor = new BrightnessMachine();
        var gpu = new GpuMachine();
        using var harness = new SwitcherHarness([monitor.Lever(), gpu.Lever()], elevated: false);

        var outcome = await harness.Switcher.SwitchAsync(PowerModeKind.Station);

        Assert.Equal(PowerModeKind.Station, harness.Switcher.State.Mode);
        Assert.Equal(1, outcome.FailedCount);
        var failed = outcome.Results[LeverKind.Gpu];
        Assert.Equal(LeverStatus.Failed, failed.Status);
        Assert.True(failed.NeedsElevation);
        Assert.Equal(LeverResult.NeedsAdministrator, failed.Detail);
        Assert.Equal(120_000u, gpu.LimitMw);
        Assert.Equal(35, monitor.Value);
    }

    [Fact]
    public async Task Failed_grid_restore_keeps_the_snapshot_and_its_part_survives_the_next_capture()
    {
        var monitor = new BrightnessMachine();
        using var harness = new SwitcherHarness([monitor.Lever()]);
        await harness.Switcher.SwitchAsync(PowerModeKind.Station);

        monitor.FailWrites = true;
        var outcome = await harness.Switcher.SwitchAsync(PowerModeKind.Grid);
        Assert.Equal(1, outcome.FailedCount);
        Assert.Equal("monitor asleep", outcome.Results[LeverKind.Brightness].Detail);
        Assert.NotNull(harness.Switcher.State.Snapshot);
        Assert.True(harness.Switcher.NeedsReapplyOnStart);

        // Leaving Grid again re-captures, but brightness keeps the old baseline (80), not the stuck 35.
        monitor.FailWrites = false;
        await harness.Switcher.SwitchAsync(PowerModeKind.Station);
        Assert.Equal(80, harness.Switcher.State.Snapshot!.Monitors.Single().Brightness);
        await harness.Switcher.SwitchAsync(PowerModeKind.Grid);
        Assert.Equal(80, monitor.Value);
        Assert.Null(harness.Switcher.State.Snapshot);
    }

    [Fact]
    public async Task Levers_run_after_their_predecessor()
    {
        var order = new List<LeverKind>();
        var display = new FakeLever(LeverKind.DisplayMode)
        {
            OnPlan = _ => LeverPlan.Of([new LeverAction("mode", async ct =>
            {
                await Task.Delay(50, ct);
                lock (order)
                {
                    order.Add(LeverKind.DisplayMode);
                }
            })]),
        };
        var brightness = new FakeLever(LeverKind.Brightness)
        {
            RunsAfter = LeverKind.DisplayMode,
            OnPlan = _ => LeverPlan.Of([new LeverAction("brightness", _ =>
            {
                lock (order)
                {
                    order.Add(LeverKind.Brightness);
                }

                return Task.CompletedTask;
            })]),
        };
        using var harness = new SwitcherHarness([brightness, display]);

        await harness.Switcher.SwitchAsync(PowerModeKind.Station);

        Assert.Equal([LeverKind.DisplayMode, LeverKind.Brightness], order);
    }

    [Fact]
    public async Task Progress_reports_start_each_lever_and_completion()
    {
        var events = new List<SwitchProgress>();
        using var harness = new SwitcherHarness([new BrightnessMachine().Lever(), new GpuMachine().Lever()]);
        harness.Switcher.Progress += (_, p) =>
        {
            lock (events)
            {
                events.Add(p);
            }
        };

        await harness.Switcher.SwitchAsync(PowerModeKind.Station);

        Assert.Equal(SwitchPhase.Started, events[0].Phase);
        Assert.Equal(2, events[0].Levers.Count);
        Assert.Equal(2, events.Count(e => e.Phase == SwitchPhase.LeverFinished));
        Assert.Equal(SwitchPhase.Completed, events[^1].Phase);
    }

    [Fact]
    public async Task Partial_reapply_touches_only_the_requested_levers()
    {
        var monitor = new BrightnessMachine();
        var gpu = new GpuMachine();
        var gpuLever = gpu.Lever();
        var monitorLever = monitor.Lever();
        using var harness = new SwitcherHarness([monitorLever, gpuLever]);
        await harness.Switcher.SwitchAsync(PowerModeKind.Station);
        gpuLever.Planned.Clear();

        monitor.Value = 70;
        Assert.True(await harness.Switcher.HasDriftedAsync([LeverKind.Brightness]));
        var outcome = await harness.Switcher.ReapplyAsync([LeverKind.Brightness]);

        Assert.Single(outcome.Results);
        Assert.Equal(35, monitor.Value);
        Assert.Empty(gpuLever.Planned);
        Assert.Equal(1, monitorLever.Captures);
        Assert.False(await harness.Switcher.HasDriftedAsync([LeverKind.Brightness]));
    }

    [Fact]
    public async Task Full_reapply_without_levers_reapplies_the_current_mode()
    {
        var monitor = new BrightnessMachine();
        var lever = monitor.Lever();
        using var harness = new SwitcherHarness([lever]);
        await harness.Switcher.SwitchAsync(PowerModeKind.Reserve);
        monitor.Value = 99;

        var outcome = await harness.Switcher.ReapplyAsync();

        Assert.Equal(PowerModeKind.Reserve, outcome.Mode);
        Assert.Equal(10, monitor.Value);
        Assert.Equal(1, lever.Captures);
    }

    [Fact]
    public async Task Dry_run_plans_without_changing_or_persisting_anything()
    {
        var monitor = new BrightnessMachine();
        var gpu = new GpuMachine();
        using var harness = new SwitcherHarness([monitor.Lever(), gpu.Lever()], elevated: false);

        var report = await harness.Switcher.DryRunAsync(PowerModeKind.Station);

        Assert.True(report.CapturesSnapshot);
        Assert.Equal(PowerModeKind.Grid, report.From);
        Assert.Equal(80, monitor.Value);
        Assert.Equal(0, monitor.Writes);
        Assert.False(File.Exists(harness.Store.Path));
        Assert.Null(harness.Switcher.State.Snapshot);
        var gpuPlan = report.Levers.Single(l => l.Kind == LeverKind.Gpu);
        Assert.Single(gpuPlan.Blocked);
        Assert.Empty(report.Levers.Single(l => l.Kind == LeverKind.Brightness).Blocked);
    }

    [Fact]
    public async Task Dry_run_from_a_saving_mode_uses_the_persisted_snapshot()
    {
        var monitor = new BrightnessMachine();
        var lever = monitor.Lever();
        using var harness = new SwitcherHarness([lever]);
        await harness.Switcher.SwitchAsync(PowerModeKind.Station);

        var report = await harness.Switcher.DryRunAsync(PowerModeKind.Grid);

        Assert.False(report.CapturesSnapshot);
        Assert.Equal(1, lever.Captures);
        Assert.Equal(80, report.Target.Monitor!.Restore.Single().Brightness);
    }

    [Fact]
    public async Task Keep_docker_option_is_persisted_per_mode()
    {
        var docker = new FakeLever(LeverKind.Docker)
        {
            OnCapture = s => s.Docker = new DockerSnapshot(true, ["a", "b"]),
            OnPlan = c => c.Target.Docker is null ? LeverPlan.None : LeverPlan.Done,
        };
        using var harness = new SwitcherHarness([docker]);

        await harness.Switcher.SetKeepDockerAsync(PowerModeKind.Station, true);
        await harness.Switcher.SwitchAsync(PowerModeKind.Station);

        Assert.True(harness.Reload().OptionsFor(PowerModeKind.Station).KeepDocker);
        Assert.False(harness.Reload().OptionsFor(PowerModeKind.Reserve).KeepDocker);
        Assert.True(docker.Planned.Single().Docker!.Running);
        Assert.Equal(["a", "b"], docker.Planned.Single().Docker!.ContainersToStart);
    }

    [Fact]
    public async Task Grid_clears_the_closed_apps_list_with_the_snapshot()
    {
        using var harness = new SwitcherHarness([new BrightnessMachine().Lever()]);
        await harness.Switcher.SwitchAsync(PowerModeKind.Station);
        harness.Switcher.State.ClosedApps.Add("steam");

        await harness.Switcher.SwitchAsync(PowerModeKind.Grid);

        Assert.Empty(harness.Reload().ClosedApps);
    }

    [Fact]
    public void Order_by_dependency_puts_predecessors_first()
    {
        var a = new FakeLever(LeverKind.Brightness) { RunsAfter = LeverKind.DisplayMode };
        var b = new FakeLever(LeverKind.DisplayMode);
        var c = new FakeLever(LeverKind.Gpu) { RunsAfter = LeverKind.Docker };

        var ordered = ModeSwitcher.OrderByDependency([a, b, c]);

        Assert.True(ordered.IndexOf(b) < ordered.IndexOf(a));
        Assert.Contains(c, ordered);
    }

    [Fact]
    public void Drift_guard_allows_one_correction_per_minute()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var guard = new DriftGuard(time);

        Assert.True(guard.TryEnter());
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(guard.TryEnter());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(guard.TryEnter());
    }
}
