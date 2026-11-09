using System.Diagnostics;
using System.Globalization;
using PowerMode.Core.Config;
using PowerMode.Core.Logging;
using PowerMode.Core.Model;

namespace PowerMode.Core.Levers;

/// <summary>
/// Runs a lever: plan, elevation gating, actions with a timeout, logging of start/result/duration.
/// This is the single place that turns every exception into a <see cref="LeverResult"/>.
/// </summary>
public sealed class LeverExecutor
{
    public const string Category = "lever";
    public static readonly TimeSpan DefaultActionTimeout = TimeSpan.FromSeconds(300);
    private const string TimedOut = "Timed out";
    private const string Cancelled = "Cancelled";
    private const string Separator = "; ";

    private readonly ILog _log;
    private readonly TimeSpan _actionTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="log">The app log.</param>
    /// <param name="actionTimeout">Longest one action may take.</param>
    /// <param name="delay">How <see cref="LeverRetry"/> waits; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> by default.</param>
    public LeverExecutor(ILog log, TimeSpan? actionTimeout = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _log = log;
        _actionTimeout = actionTimeout ?? DefaultActionTimeout;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Capture one lever's part of the baseline. False when the read failed or was inconclusive (the lever threw).</summary>
    public async Task<bool> CaptureAsync(ILever lever, Snapshot snapshot, AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lever);
        var watch = Stopwatch.StartNew();
        try
        {
            await lever.CaptureAsync(snapshot, config, cancellationToken).ConfigureAwait(false);
            _log.Write(Category, $"{lever.Kind}: captured baseline ({Ms(watch)})");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log.Error(Category, $"{lever.Kind}: capture failed ({Ms(watch)})", ex);
            return false;
        }
    }

    public async Task<LeverPlan> PlanAsync(ILever lever, ApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lever);
        try
        {
            return await lever.PlanAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log.Error(Category, $"{lever.Kind}: cannot read current state", ex);
            return LeverPlan.Fail(ex is LeverException ? ex.Message : $"Cannot read current state: {ex.Message}");
        }
    }

    public Task<LeverResult> ApplyAsync(ILever lever, ApplyContext context, CancellationToken cancellationToken) =>
        ApplyAsync(lever, context, predecessorChanged: false, cancellationToken);

    /// <param name="lever">The lever to apply.</param>
    /// <param name="context">Target, state and elevation.</param>
    /// <param name="predecessorChanged">The lever's <see cref="ILever.RunsAfter"/> predecessor changed the machine in this apply.</param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    public async Task<LeverResult> ApplyAsync(ILever lever, ApplyContext context, bool predecessorChanged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lever);
        ArgumentNullException.ThrowIfNull(context);
        var watch = Stopwatch.StartNew();
        _log.Write(Category, $"{lever.Kind}: start ({ModeNames.Display(context.Target.Mode)})");
        LeverResult result;
        try
        {
            result = await RunAsync(lever, context, predecessorChanged, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = LeverResult.Failed(Cancelled);
        }

        result = result with { Duration = watch.Elapsed };
        _log.Write(Category, $"{lever.Kind}: {result.Status} - {result.Detail} ({Ms(watch)})");
        return result;
    }

    private async Task<LeverResult> RunAsync(ILever lever, ApplyContext context, bool predecessorChanged, CancellationToken cancellationToken)
    {
        var plan = await PlanWithRetryAsync(lever, context, predecessorChanged, cancellationToken).ConfigureAwait(false);
        if (plan.Unavailable is { } unavailable)
        {
            return LeverResult.Failed(unavailable) with { Permanent = plan.Permanent };
        }

        if (plan.NotOwned)
        {
            return LeverResult.Skipped(plan.SkipReason ?? LeverResult.NotOwned);
        }

        if (plan.Actions.Count == 0)
        {
            return LeverResult.Ok(LeverResult.AlreadySet);
        }

        var done = new List<string>();
        var failures = new List<(string Reason, bool NeedsElevation)>();
        foreach (var action in plan.Actions)
        {
            if (action.RequiresElevation && !context.IsElevated)
            {
                _log.Write(Category, $"{lever.Kind}: skipped \"{action.Description}\" - {LeverResult.NeedsAdministrator}");
                failures.Add((LeverResult.NeedsAdministrator, true));
                continue;
            }

            var failure = await ExecuteAsync(lever.Kind, action, cancellationToken).ConfigureAwait(false);
            if (failure is { } f)
            {
                failures.Add(f);
            }
            else
            {
                done.Add(action.Description);
            }
        }

        var changed = done.Count > 0;
        if (failures.Count == 0)
        {
            return LeverResult.Ok(string.Join(Separator, done)) with { Changed = changed };
        }

        var reason = string.Join(Separator, failures.Select(f => f.Reason).Distinct(StringComparer.Ordinal));
        return LeverResult.Failed(reason, failures.All(f => f.NeedsElevation)) with { Changed = changed };
    }

    /// <summary>Plan once, or per the lever's <see cref="LeverRetry"/>: settle after a predecessor's change, retry while unavailable.</summary>
    private async Task<LeverPlan> PlanWithRetryAsync(ILever lever, ApplyContext context, bool predecessorChanged, CancellationToken cancellationToken)
    {
        if (lever.Retry is not { } retry)
        {
            return await PlanAsync(lever, context, cancellationToken).ConfigureAwait(false);
        }

        if (predecessorChanged)
        {
            _log.Write(Category, $"{lever.Kind}: {lever.RunsAfter} changed, waiting {Ms(retry.Settle)} for it to settle");
            await _delay(retry.Settle, cancellationToken).ConfigureAwait(false);
        }

        for (var attempt = 1; ; attempt++)
        {
            var plan = await PlanAsync(lever, context, cancellationToken).ConfigureAwait(false);
            if (plan.Unavailable is not { } reason || plan.Permanent || attempt > retry.Retries)
            {
                return plan;
            }

            _log.Write(Category, $"{lever.Kind}: {reason}; retry {attempt} of {retry.Retries} in {Ms(retry.Delay)}");
            await _delay(retry.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(string Reason, bool NeedsElevation)?> ExecuteAsync(LeverKind kind, LeverAction action, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_actionTimeout);
        try
        {
            await action.Execute(timeout.Token).ConfigureAwait(false);
            _log.Write(Category, $"{kind}: {action.Description} - ok ({Ms(watch)})");
            return null;
        }
        catch (LeverException ex)
        {
            _log.Write(Category, $"{kind}: {action.Description} - failed: {ex.Message} ({Ms(watch)})");
            return (ex.Message, ex.NeedsElevation);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Write(Category, $"{kind}: {action.Description} - timed out ({Ms(watch)})");
            return (TimedOut, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(Category, $"{kind}: {action.Description} - failed ({Ms(watch)})", ex);
            return (ex.Message, false);
        }
    }

    private static string Ms(Stopwatch watch) => Ms(watch.Elapsed);

    private static string Ms(TimeSpan span) => ((long)span.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms";
}
