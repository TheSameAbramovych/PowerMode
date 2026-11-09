using PowerMode.Core.Levers;
using PowerMode.Core.Model;
using PowerMode.Core.State;

namespace PowerMode.Core.Tests;

/// <summary>Brightness right after a refresh-rate change: settle, retry while the monitor re-syncs, retry later if still owed.</summary>
public sealed class DisplaySettleTests
{
    private static readonly LeverKind[] DisplayLevers = [LeverKind.DisplayMode, LeverKind.Brightness];
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Brightness_waits_for_the_monitor_to_resync_after_a_mode_change()
    {
        using var harness = new SwitcherHarness([]);
        var machine = new ResyncingMonitor(harness.Time);
        using var switcher = Switcher(harness, machine);

        var outcome = await switcher.SwitchAsync(PowerModeKind.Station);

        Assert.Equal(60, machine.RefreshHz);
        Assert.Equal(35, machine.Brightness);
        Assert.Equal(0, outcome.FailedCount);
        Assert.True(outcome.Results[LeverKind.DisplayMode].Changed);

        // 2 s settle, silent at 2 s, answers at 3 s.
        Assert.Equal([Settle, Second], harness.Delay.Waits);
        Assert.Equal(2, machine.BrightnessPlans);
        Assert.False(switcher.OwesRetry(DisplayLevers));
    }

    [Fact]
    public async Task No_settle_when_the_display_mode_was_already_set()
    {
        using var harness = new SwitcherHarness([]);
        var machine = new ResyncingMonitor(harness.Time) { RefreshHz = 59 };
        using var switcher = Switcher(harness, machine);

        var outcome = await switcher.SwitchAsync(PowerModeKind.Station);

        Assert.Equal(LeverResult.AlreadySet, outcome.Results[LeverKind.DisplayMode].Detail);
        Assert.False(outcome.Results[LeverKind.DisplayMode].Changed);
        Assert.Equal(59, machine.RefreshHz);
        Assert.Equal(35, machine.Brightness);
        Assert.Empty(harness.Delay.Waits);
        Assert.Equal(1, machine.BrightnessPlans);
    }

    [Fact]
    public async Task A_monitor_that_stays_silent_fails_after_six_retries_and_is_owed_a_retry()
    {
        using var harness = new SwitcherHarness([]);
        var machine = new ResyncingMonitor(harness.Time) { Resync = TimeSpan.FromMinutes(1) };
        using var switcher = Switcher(harness, machine);

        var outcome = await switcher.SwitchAsync(PowerModeKind.Station);

        Assert.Equal([Settle, Second, Second, Second, Second, Second, Second], harness.Delay.Waits);
        Assert.Equal(7, machine.BrightnessPlans);
        var brightness = outcome.Results[LeverKind.Brightness];
        Assert.Equal(ResyncingMonitor.NoMonitor, brightness.Detail);
        Assert.True(brightness.IsRetryable);
        Assert.Equal(80, machine.Brightness);
        Assert.Contains(harness.Log.Lines, l => l.Contains("Brightness: DisplayMode changed, waiting 2000 ms", StringComparison.Ordinal));
        Assert.Contains(harness.Log.Lines, l => l.Contains("retry 6 of 6", StringComparison.Ordinal));

        // The tray checks the display levers again although the config does not react to display changes.
        Assert.True(switcher.OwesRetry(DisplayLevers));
        Assert.True(switcher.WantsDisplayCheck(DisplayLevers, reapplyOnDisplayChange: false));
        Assert.True(await switcher.HasDriftedAsync(DisplayLevers));

        harness.Time.Advance(TimeSpan.FromMinutes(1));
        harness.Delay.Waits.Clear();
        var retry = await switcher.ReapplyAsync(DisplayLevers);

        Assert.Equal(0, retry.FailedCount);
        Assert.Equal(35, machine.Brightness);
        Assert.Empty(harness.Delay.Waits);
        Assert.False(switcher.OwesRetry(DisplayLevers));
        Assert.False(await switcher.HasDriftedAsync(DisplayLevers));
        Assert.False(switcher.WantsDisplayCheck(DisplayLevers, reapplyOnDisplayChange: false));
        Assert.True(switcher.WantsDisplayCheck(DisplayLevers, reapplyOnDisplayChange: true));
    }

    [Fact]
    public async Task A_grid_restore_owing_only_brightness_is_retried_by_the_display_check()
    {
        using var harness = new SwitcherHarness([]);
        var machine = new ResyncingMonitor(harness.Time);
        using var switcher = Switcher(harness, machine);
        await switcher.SwitchAsync(PowerModeKind.Station);

        machine.Resync = TimeSpan.FromMinutes(1);
        var grid = await switcher.SwitchAsync(PowerModeKind.Grid);

        Assert.Equal(200, machine.RefreshHz);
        Assert.True(grid.Results[LeverKind.Brightness].IsFailure);
        Assert.Contains(harness.Log.Lines, l => l.Contains("Baseline kept for Brightness: not restored yet", StringComparison.Ordinal));
        Assert.True(switcher.WantsDisplayCheck(DisplayLevers, reapplyOnDisplayChange: false));
        Assert.True(await switcher.HasDriftedAsync(DisplayLevers));

        harness.Time.Advance(TimeSpan.FromMinutes(1));
        var retry = await switcher.ReapplyAsync(DisplayLevers);

        Assert.Equal(0, retry.FailedCount);
        Assert.Equal(80, machine.Brightness);
        Assert.Null(harness.Reload().Snapshot);
        Assert.False(switcher.NeedsReapplyOnStart);
        Assert.False(switcher.WantsDisplayCheck(DisplayLevers, reapplyOnDisplayChange: true));
    }

    [Fact]
    public async Task Grid_with_nothing_owed_never_checks_the_display()
    {
        using var harness = new SwitcherHarness([]);
        var machine = new ResyncingMonitor(harness.Time);
        using var switcher = Switcher(harness, machine);

        Assert.False(switcher.WantsDisplayCheck(DisplayLevers, reapplyOnDisplayChange: true));
        Assert.False(await switcher.HasDriftedAsync(DisplayLevers));
    }

    [Fact]
    public async Task A_permanent_failure_is_not_retried()
    {
        var delay = new FakeDelay(new ManualTime(DateTimeOffset.UnixEpoch));
        var plans = 0;
        var lever = new FakeLever(LeverKind.Brightness)
        {
            Retry = ResyncingMonitor.Retry,
            OnPlan = _ =>
            {
                plans++;
                return LeverPlan.Fail("No DDC/CI on this machine", permanent: true);
            },
        };

        var result = await new LeverExecutor(new ListLog(), delay: delay.Wait).ApplyAsync(lever, Context(), predecessorChanged: true, default);

        Assert.True(result.IsFailure);
        Assert.False(result.IsRetryable);
        Assert.Equal(1, plans);
        Assert.Equal([Settle], delay.Waits);
    }

    [Fact]
    public async Task A_lever_without_a_retry_policy_plans_once_and_never_settles()
    {
        var delay = new FakeDelay(new ManualTime(DateTimeOffset.UnixEpoch));
        var plans = 0;
        var lever = new FakeLever(LeverKind.Gpu)
        {
            OnPlan = _ =>
            {
                plans++;
                return LeverPlan.Fail("NVML did not answer");
            },
        };

        var result = await new LeverExecutor(new ListLog(), delay: delay.Wait).ApplyAsync(lever, Context(), predecessorChanged: true, default);

        Assert.Equal("NVML did not answer", result.Detail);
        Assert.Equal(1, plans);
        Assert.Empty(delay.Waits);
    }

    [Fact]
    public async Task Cancelling_while_waiting_reports_cancelled()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var lever = new FakeLever(LeverKind.Brightness) { Retry = ResyncingMonitor.Retry, OnPlan = _ => LeverPlan.Fail(ResyncingMonitor.NoMonitor) };

        var result = await new LeverExecutor(new ListLog(), delay: new FakeDelay(new ManualTime(DateTimeOffset.UnixEpoch)).Wait)
            .ApplyAsync(lever, Context(), predecessorChanged: true, cancel.Token);

        Assert.Equal("Cancelled", result.Detail);
    }

    [Fact]
    public async Task Changed_says_whether_any_action_ran()
    {
        var executor = new LeverExecutor(new ListLog());
        var ok = new LeverAction("a", _ => Task.CompletedTask);
        var fail = new LeverAction("b", _ => throw new LeverException("refused"));

        Assert.True((await executor.ApplyAsync(new FakeLever(LeverKind.Services) { OnPlan = _ => LeverPlan.Of([ok]) }, Context(), default)).Changed);
        Assert.True((await executor.ApplyAsync(new FakeLever(LeverKind.Services) { OnPlan = _ => LeverPlan.Of([ok, fail]) }, Context(), default)).Changed);
        Assert.False((await executor.ApplyAsync(new FakeLever(LeverKind.Services) { OnPlan = _ => LeverPlan.Of([fail]) }, Context(), default)).Changed);
        Assert.False((await executor.ApplyAsync(new FakeLever(LeverKind.Services) { OnPlan = _ => LeverPlan.Done }, Context(), default)).Changed);
    }

    [Fact]
    public void Only_failures_that_may_go_away_are_retryable()
    {
        Assert.True(LeverResult.Failed(ResyncingMonitor.NoMonitor).IsRetryable);
        Assert.False(LeverResult.Failed(LeverResult.NeedsAdministrator, needsElevation: true).IsRetryable);
        Assert.False((LeverResult.Failed("Docker is not installed") with { Permanent = true }).IsRetryable);
        Assert.False(LeverResult.Failed(LeverResult.BaselineNotCaptured).IsRetryable);
        Assert.False(LeverResult.Ok("x").IsRetryable);
        Assert.False(LeverResult.Skipped(LeverResult.NotOwned).IsRetryable);
    }

    [Fact]
    public void Changed_and_retryable_are_not_written_to_the_state_file()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(LeverResult.Ok("x") with { Changed = true }, StateJson.Options);

        Assert.DoesNotContain("changed", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("retryable", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The harness's store, clock and fake delays with the re-syncing display and monitor.</summary>
    private static Switching.ModeSwitcher Switcher(SwitcherHarness harness, ResyncingMonitor machine) =>
        new([machine.Display(), machine.Monitor()], harness.Store, () => harness.Config, () => true, harness.Time, harness.Log, new LeverExecutor(harness.Log, delay: harness.Delay.Wait));

    private static ApplyContext Context() => new(new TargetState { Mode = PowerModeKind.Station }, new PersistedState(), true);
}
