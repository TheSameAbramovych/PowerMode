using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;
using PowerMode.Core.State;

namespace PowerMode.Core.Tests;

public sealed class LeverExecutorTests
{
    private static readonly ApplyContext Elevated = Context(true);
    private static readonly ApplyContext NotElevated = Context(false);

    private static ApplyContext Context(bool elevated) =>
        new(new TargetState { Mode = PowerModeKind.Station }, new PersistedState(), elevated);

    private static FakeLever Lever(params LeverAction[] actions) => new(LeverKind.Services) { OnPlan = _ => LeverPlan.Of(actions) };

    private static LeverAction Ok(string name) => new(name, _ => Task.CompletedTask);

    [Fact]
    public async Task All_actions_ok_reports_ok_with_what_was_done()
    {
        var log = new ListLog();
        var result = await new LeverExecutor(log).ApplyAsync(Lever(Ok("a"), Ok("b")), Elevated, default);

        Assert.Equal(LeverStatus.Ok, result.Status);
        Assert.Equal("a; b", result.Detail);
        Assert.Contains(log.Lines, l => l.Contains("Services: start", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Contains("Services: Ok", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_actions_means_already_set()
    {
        var result = await new LeverExecutor(new ListLog()).ApplyAsync(new FakeLever(LeverKind.Gpu) { OnPlan = _ => LeverPlan.Done }, Elevated, default);

        Assert.Equal(LeverResult.Ok(LeverResult.AlreadySet) with { Duration = result.Duration }, result);
    }

    [Fact]
    public async Task Not_owned_is_skipped()
    {
        var result = await new LeverExecutor(new ListLog()).ApplyAsync(new FakeLever(LeverKind.Gpu), Elevated, default);

        Assert.Equal(LeverStatus.Skipped, result.Status);
        Assert.False(result.IsFailure);
    }

    [Fact]
    public async Task Unavailable_lever_fails_with_its_reason()
    {
        var lever = new FakeLever(LeverKind.Docker) { OnPlan = _ => LeverPlan.Fail("Docker is not installed") };

        var result = await new LeverExecutor(new ListLog()).ApplyAsync(lever, Elevated, default);

        Assert.Equal(LeverStatus.Failed, result.Status);
        Assert.Equal("Docker is not installed", result.Detail);
        Assert.False(result.NeedsElevation);
    }

    [Fact]
    public async Task Elevated_actions_are_gated_when_not_elevated_and_others_still_run()
    {
        var ran = new List<string>();
        var lever = Lever(
            new LeverAction("admin", _ =>
            {
                ran.Add("admin");
                return Task.CompletedTask;
            }, RequiresElevation: true),
            new LeverAction("plain", _ =>
            {
                ran.Add("plain");
                return Task.CompletedTask;
            }));

        var result = await new LeverExecutor(new ListLog()).ApplyAsync(lever, NotElevated, default);

        Assert.Equal(["plain"], ran);
        Assert.Equal(LeverStatus.Failed, result.Status);
        Assert.True(result.NeedsElevation);
        Assert.Equal(LeverResult.NeedsAdministrator, result.Detail);
    }

    [Fact]
    public async Task Elevated_actions_run_when_elevated()
    {
        var result = await new LeverExecutor(new ListLog()).ApplyAsync(Lever(new LeverAction("admin", _ => Task.CompletedTask, RequiresElevation: true)), Elevated, default);

        Assert.Equal(LeverStatus.Ok, result.Status);
    }

    [Fact]
    public async Task Exceptions_never_escape_and_mixed_reasons_do_not_claim_elevation()
    {
        var lever = Lever(
            new LeverAction("lever exception", _ => throw new LeverException("refused")),
            new LeverAction("random exception", _ => throw new InvalidOperationException("boom")),
            new LeverAction("admin", _ => Task.CompletedTask, RequiresElevation: true));

        var result = await new LeverExecutor(new ListLog()).ApplyAsync(lever, NotElevated, default);

        Assert.Equal(LeverStatus.Failed, result.Status);
        Assert.Equal("refused; boom; Needs administrator", result.Detail);
        Assert.False(result.NeedsElevation);
    }

    [Fact]
    public async Task Lever_exception_can_flag_elevation()
    {
        var lever = Lever(new LeverAction("x", _ => throw new LeverException(LeverResult.NeedsAdministrator, needsElevation: true)));

        var result = await new LeverExecutor(new ListLog()).ApplyAsync(lever, Elevated, default);

        Assert.True(result.NeedsElevation);
    }

    [Fact]
    public async Task Slow_actions_time_out()
    {
        var lever = Lever(new LeverAction("slow", ct => Task.Delay(TimeSpan.FromSeconds(30), ct)));

        var result = await new LeverExecutor(new ListLog(), TimeSpan.FromMilliseconds(50)).ApplyAsync(lever, Elevated, default);

        Assert.Equal("Timed out", result.Detail);
    }

    [Fact]
    public async Task Cancellation_is_reported_not_thrown()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var lever = Lever(new LeverAction("x", ct => Task.Delay(1000, ct)));

        var result = await new LeverExecutor(new ListLog()).ApplyAsync(lever, Elevated, cts.Token);

        Assert.Equal(LeverStatus.Failed, result.Status);
        Assert.Equal("Cancelled", result.Detail);
    }

    [Fact]
    public async Task Plan_exceptions_become_failures()
    {
        var lever = new FakeLever(LeverKind.Gpu) { OnPlan = _ => throw new InvalidOperationException("nvml gone") };
        var lever2 = new FakeLever(LeverKind.Gpu) { OnPlan = _ => throw new LeverException("readable") };
        var executor = new LeverExecutor(new ListLog());

        Assert.Equal("Cannot read current state: nvml gone", (await executor.ApplyAsync(lever, Elevated, default)).Detail);
        Assert.Equal("readable", (await executor.ApplyAsync(lever2, Elevated, default)).Detail);
    }

    [Fact]
    public async Task Capture_errors_are_logged_and_swallowed()
    {
        var log = new ListLog();
        var lever = new FakeLever(LeverKind.Gpu) { OnCapture = _ => throw new InvalidOperationException("no gpu") };

        await new LeverExecutor(log).CaptureAsync(lever, new Snapshot(), AppConfig.CreateDefault(), default);

        Assert.Contains(log.Lines, l => l.Contains("capture failed", StringComparison.Ordinal) && l.Contains("no gpu", StringComparison.Ordinal));
    }

    [Fact]
    public void Lever_exception_constructors()
    {
        Assert.NotEmpty(new LeverException().Message);
        var inner = new InvalidOperationException("inner");
        var wrapped = new LeverException("outer", inner);
        Assert.Same(inner, wrapped.InnerException);
        Assert.False(wrapped.NeedsElevation);
        Assert.False(new LeverException("m").NeedsElevation);
    }
}
