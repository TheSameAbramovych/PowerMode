using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Logging;
using PowerMode.Core.Model;
using PowerMode.Core.Planning;
using PowerMode.Core.State;

namespace PowerMode.Core.Switching;

public enum SwitchPhase
{
    Started,
    LeverFinished,
    Completed,
}

/// <summary>Progress of a switch, raised on the thread that runs the lever.</summary>
public sealed record SwitchProgress(SwitchPhase Phase, PowerModeKind Mode, IReadOnlySet<LeverKind> Levers, LeverKind? Lever = null, LeverResult? Result = null);

public sealed record SwitchOutcome(PowerModeKind Mode, IReadOnlyDictionary<LeverKind, LeverResult> Results)
{
    public int FailedCount => Results.Values.Count(r => r.IsFailure);
}

public sealed record DryRunLever(LeverKind Kind, LeverPlan Plan, bool IsElevated)
{
    /// <summary>Actions that would be refused because the process is not elevated.</summary>
    public IEnumerable<LeverAction> Blocked => Plan.Actions.Where(a => a.RequiresElevation && !IsElevated);
}

public sealed record DryRunReport(PowerModeKind From, PowerModeKind To, bool CapturesSnapshot, TargetState Target, IReadOnlyList<DryRunLever> Levers);

/// <summary>
/// Applies modes: snapshot when leaving Grid (persisted at once), plan the target state, run the levers in
/// parallel (respecting <see cref="ILever.RunsAfter"/>), persist the outcome. One switch at a time.
/// </summary>
public sealed class ModeSwitcher : IDisposable
{
    private const string Category = "switch";

    private readonly IReadOnlyList<ILever> _levers;
    private readonly StateStore _store;
    private readonly Func<AppConfig> _config;
    private readonly Func<bool> _isElevated;
    private readonly TimeProvider _time;
    private readonly ILog _log;
    private readonly LeverExecutor _executor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ModeSwitcher(IReadOnlyList<ILever> levers, StateStore store, Func<AppConfig> config, Func<bool> isElevated, TimeProvider time, ILog log, LeverExecutor? executor = null)
    {
        _levers = levers;
        _store = store;
        _config = config;
        _isElevated = isElevated;
        _time = time;
        _log = log;
        _executor = executor ?? new LeverExecutor(log);
        State = store.Load();
    }

    public PersistedState State { get; }

    public IReadOnlyList<ILever> Levers => _levers;

    public event EventHandler<SwitchProgress>? Progress;

    /// <summary>On start: re-apply a saving mode (the GPU limit does not survive a reboot) or retry an unfinished Grid restore.</summary>
    public bool NeedsReapplyOnStart => State.Mode != PowerModeKind.Grid || State.Snapshot is not null;

    public async Task<SwitchOutcome> SwitchAsync(PowerModeKind mode, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = _config();
            var from = State.Mode;
            _log.Write(Category, $"{ModeNames.Display(from)} -> {ModeNames.Display(mode)}");
            if (mode != PowerModeKind.Grid && from == PowerModeKind.Grid)
            {
                State.Snapshot = await CaptureAsync(config, State.Snapshot, cancellationToken).ConfigureAwait(false);
                _store.Save(State);
                _log.Write(Category, "Baseline snapshot saved");
            }

            var outcome = await ApplyAsync(mode, config, null, cancellationToken).ConfigureAwait(false);
            if (mode != from || State.Since is null)
            {
                State.Since = _time.GetUtcNow();
            }

            State.Mode = mode;
            if (mode == PowerModeKind.Grid && outcome.FailedCount == 0 && State.Snapshot is not null)
            {
                State.Snapshot = null;
                _log.Write(Category, "Baseline restored and cleared");
            }

            _store.Save(State);
            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Re-apply the current mode, optionally only some levers (resume, display change, option toggle).</summary>
    public async Task<SwitchOutcome> ReapplyAsync(IReadOnlyCollection<LeverKind>? only = null, CancellationToken cancellationToken = default)
    {
        if (only is null)
        {
            return await SwitchAsync(State.Mode, cancellationToken).ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await ApplyAsync(State.Mode, _config(), only, cancellationToken).ConfigureAwait(false);
            _store.Save(State);
            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>True when any of these levers is no longer at the current mode's target (read-only).</summary>
    public async Task<bool> HasDriftedAsync(IReadOnlyCollection<LeverKind> levers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(levers);
        var context = Context(State.Mode, _config(), State.Snapshot);
        foreach (var lever in _levers.Where(l => levers.Contains(l.Kind)))
        {
            var plan = await _executor.PlanAsync(lever, context, cancellationToken).ConfigureAwait(false);
            if (plan.Actions.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What switching to <paramref name="mode"/> would change. Reads only; persists nothing.</summary>
    public async Task<DryRunReport> DryRunAsync(PowerModeKind mode, CancellationToken cancellationToken = default)
    {
        var config = _config();
        var from = State.Mode;
        var captures = mode != PowerModeKind.Grid && from == PowerModeKind.Grid;
        var snapshot = captures ? await CaptureAsync(config, State.Snapshot, cancellationToken).ConfigureAwait(false) : State.Snapshot;
        var context = Context(mode, config, snapshot);
        var levers = new List<DryRunLever>();
        foreach (var lever in _levers)
        {
            var plan = await _executor.PlanAsync(lever, context, cancellationToken).ConfigureAwait(false);
            levers.Add(new DryRunLever(lever.Kind, plan, context.IsElevated));
        }

        return new DryRunReport(from, mode, captures, context.Target, levers);
    }

    public void SetKeepDocker(PowerModeKind mode, bool keep)
    {
        State.OptionsFor(mode).KeepDocker = keep;
        _store.Save(State);
    }

    private async Task<Snapshot> CaptureAsync(AppConfig config, Snapshot? retained, CancellationToken cancellationToken)
    {
        var snapshot = new Snapshot { CapturedAt = _time.GetUtcNow() };
        await Task.WhenAll(_levers.Select(l => _executor.CaptureAsync(l, snapshot, config, cancellationToken))).ConfigureAwait(false);
        if (retained is not null)
        {
            // A previous Grid restore failed for these levers: the machine still holds reduced values, so keep the old baseline.
            foreach (var (kind, result) in State.LastResults.Where(r => r.Value.IsFailure))
            {
                snapshot.CopyPartFrom(retained, kind);
                _log.Write(Category, $"{kind}: kept the previous baseline (its restore had failed)");
            }
        }

        return snapshot;
    }

    private ApplyContext Context(PowerModeKind mode, AppConfig config, Snapshot? snapshot) =>
        new(TargetPlanner.Plan(mode, config, snapshot, State.OptionsFor(mode)), State, _isElevated());

    private async Task<SwitchOutcome> ApplyAsync(PowerModeKind mode, AppConfig config, IReadOnlyCollection<LeverKind>? only, CancellationToken cancellationToken)
    {
        var context = Context(mode, config, State.Snapshot);
        var selected = _levers.Where(l => only is null || only.Contains(l.Kind)).ToList();
        var kinds = selected.Select(l => l.Kind).ToHashSet();
        Progress?.Invoke(this, new SwitchProgress(SwitchPhase.Started, mode, kinds));

        var tasks = new Dictionary<LeverKind, Task<LeverResult>>();
        foreach (var lever in OrderByDependency(selected))
        {
            var predecessor = lever.RunsAfter is { } after && tasks.TryGetValue(after, out var task) ? task : null;
            tasks[lever.Kind] = RunLeverAsync(lever, context, predecessor, kinds, cancellationToken);
        }

        await Task.WhenAll(tasks.Values).ConfigureAwait(false);
        var results = tasks.ToDictionary(t => t.Key, t => t.Value.Result);
        foreach (var (kind, result) in results)
        {
            State.LastResults[kind] = result;
        }

        State.LastApplied = _time.GetUtcNow();
        var outcome = new SwitchOutcome(mode, results);
        _log.Write(Category, $"{ModeNames.Display(mode)} applied, {outcome.FailedCount} lever(s) failed");
        Progress?.Invoke(this, new SwitchProgress(SwitchPhase.Completed, mode, kinds));
        return outcome;
    }

    private async Task<LeverResult> RunLeverAsync(ILever lever, ApplyContext context, Task<LeverResult>? predecessor, IReadOnlySet<LeverKind> kinds, CancellationToken cancellationToken)
    {
        if (predecessor is not null)
        {
            await predecessor.ConfigureAwait(false);
        }
        else
        {
            await Task.Yield();
        }

        var result = await _executor.ApplyAsync(lever, context, cancellationToken).ConfigureAwait(false);
        Progress?.Invoke(this, new SwitchProgress(SwitchPhase.LeverFinished, context.Target.Mode, kinds, lever.Kind, result));
        return result;
    }

    /// <summary>Levers whose predecessor is in the list come after it, so its task exists when they are scheduled.</summary>
    internal static List<ILever> OrderByDependency(IReadOnlyList<ILever> levers)
    {
        var ordered = new List<ILever>();
        var pending = levers.ToList();
        while (pending.Count > 0)
        {
            var ready = pending.First(l => l.RunsAfter is not { } after || !pending.Exists(p => p.Kind == after));
            ordered.Add(ready);
            pending.Remove(ready);
        }

        return ordered;
    }

    public void Dispose() => _gate.Dispose();
}
