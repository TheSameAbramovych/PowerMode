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
/// parallel (respecting <see cref="ILever.RunsAfter"/>), persist the outcome. One switch at a time; every state
/// write happens under the same gate, so nothing serializes the state while a lever is changing it.
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
            if (mode == PowerModeKind.Grid)
            {
                SettleGridRestore(outcome);
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
            if (State.Mode == PowerModeKind.Grid)
            {
                // A retried part of the Grid restore (brightness after the monitor answered again) is no longer owed.
                SettleGridRestore(outcome);
            }

            _store.Save(State);
            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Change "Keep Docker running" of a mode. The option is visible at once; it is saved and, when that mode is
    /// current, Docker is re-applied once any running switch (which planned with the old option) has finished.
    /// </summary>
    public async Task SetKeepDockerAsync(PowerModeKind mode, bool keep, CancellationToken cancellationToken = default)
    {
        State.WithOptions(mode, new ModeOptions { KeepDocker = keep });
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _store.Save(State);
            if (State.Mode == mode)
            {
                await ApplyAsync(mode, _config(), [LeverKind.Docker], cancellationToken).ConfigureAwait(false);
                _store.Save(State);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// After a change on the Apps settings page (the config already holds the new lists): re-apply the levers that
    /// <paramref name="leversFor"/> returns for the current mode (see <see cref="Settings.AppSettings.ImmediateLevers"/>).
    /// The mode is read once any running switch, which planned with the old lists, has finished. Null when nothing
    /// was re-applied.
    /// </summary>
    public async Task<SwitchOutcome?> ApplySettingChangeAsync(Func<PowerModeKind, IReadOnlyList<LeverKind>> leversFor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(leversFor);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var only = leversFor(State.Mode);
            if (only.Count == 0)
            {
                return null;
            }

            _log.Write(Category, $"Apps settings changed; re-applying {string.Join(", ", only)}");
            var outcome = await ApplyAsync(State.Mode, _config(), only, cancellationToken).ConfigureAwait(false);
            _store.Save(State);
            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The saved state was lost (state.json unreadable or deleted) while a PowerMode scheme is still active, so the PC
    /// is still in that saving mode. Adopt it: start-up re-applies it, and Grid later leaves the PowerMode scheme for
    /// the fallback scheme. The rest of the baseline is gone; Grid leaves those levers as they are.
    /// </summary>
    public async Task<bool> RecoverLostModeAsync(Guid? activeScheme, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = _config();
            PowerModeKind? adopted = activeScheme == config.Modes.Station.Scheme.Id ? PowerModeKind.Station
                : activeScheme == config.Modes.Reserve.Scheme.Id ? PowerModeKind.Reserve
                : null;
            if (State.Mode != PowerModeKind.Grid || State.Snapshot is not null || adopted is not { } mode)
            {
                return false;
            }

            var now = _time.GetUtcNow();
            State.Mode = mode;
            State.Since = now;
            State.Snapshot = new Snapshot
            {
                CapturedAt = now,
                Missing = [LeverKind.PowerScheme],
                Restored = [.. Enum.GetValues<LeverKind>().Where(k => k != LeverKind.PowerScheme)],
            };
            _store.Save(State);
            _log.Write(Category, $"The saved state was lost while the {ModeNames.Display(mode)} scheme is active: back in {ModeNames.Display(mode)}. "
                + "Grid will activate the fallback scheme; the other levers have no baseline and stay as they are");
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The last apply of one of these levers failed in a way that may go away by itself (see <see cref="LeverResult.IsRetryable"/>).</summary>
    public bool OwesRetry(IReadOnlyCollection<LeverKind> levers)
    {
        ArgumentNullException.ThrowIfNull(levers);
        return State.LastResults.Any(r => levers.Contains(r.Key) && r.Value.IsRetryable);
    }

    /// <summary>
    /// Whether a display change (or a switch that left a display lever failed) should check the display levers: never
    /// in Grid with nothing left to restore; a retry that is owed always; drift only when the config asks for it.
    /// </summary>
    public bool WantsDisplayCheck(IReadOnlyCollection<LeverKind> levers, bool reapplyOnDisplayChange) =>
        NeedsReapplyOnStart && (reapplyOnDisplayChange || OwesRetry(levers));

    /// <summary>
    /// True when any of these levers is no longer at the current mode's target, or its last apply failed and is worth
    /// retrying (a monitor that did not answer right after a mode change). Read-only.
    /// </summary>
    public async Task<bool> HasDriftedAsync(IReadOnlyCollection<LeverKind> levers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(levers);
        if (OwesRetry(levers))
        {
            return true;
        }

        var context = Context(State.Mode, _config(), State.Snapshot);
        var blocked = Blocked(context.Target, State.Snapshot);
        foreach (var lever in _levers.Where(l => levers.Contains(l.Kind) && !blocked.Contains(l.Kind)))
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
        var snapshot = captures ? await CaptureAsync(config, State.Snapshot, cancellationToken).ConfigureAwait(false) : State.Snapshot?.Copy();
        if (mode != PowerModeKind.Grid && snapshot is not null)
        {
            await CompleteSnapshotAsync(snapshot, config, _levers, cancellationToken).ConfigureAwait(false);
        }

        var context = Context(mode, config, snapshot);
        var blocked = Blocked(context.Target, snapshot);
        var levers = new List<DryRunLever>();
        foreach (var lever in _levers)
        {
            var plan = blocked.Contains(lever.Kind)
                ? LeverPlan.Fail(LeverResult.BaselineNotCaptured)
                : await _executor.PlanAsync(lever, context, cancellationToken).ConfigureAwait(false);
            levers.Add(new DryRunLever(lever.Kind, plan, context.IsElevated));
        }

        return new DryRunReport(from, mode, captures, context.Target, levers);
    }

    /// <summary>Levers a saving mode must not reduce: their baseline part is missing (the scheme has Grid's fallback instead).</summary>
    internal static HashSet<LeverKind> Blocked(TargetState target, Snapshot? snapshot) =>
        target.Mode == PowerModeKind.Grid || snapshot is null
            ? []
            : [.. snapshot.Missing.Where(k => k != LeverKind.PowerScheme && target.Sets(k))];

    private async Task<Snapshot> CaptureAsync(AppConfig config, Snapshot? retained, CancellationToken cancellationToken)
    {
        var snapshot = new Snapshot { CapturedAt = _time.GetUtcNow() };
        var captured = await Task.WhenAll(_levers.Select(async l => (l.Kind, Ok: await CaptureLeverAsync(l, snapshot, config, cancellationToken).ConfigureAwait(false)))).ConfigureAwait(false);
        snapshot.Missing = [.. captured.Where(c => !c.Ok).Select(c => c.Kind)];
        if (retained is not null)
        {
            // Leaving Grid while an old baseline is still owed: its Grid restore failed for these parts, or the switch
            // that took it was interrupted. The machine may still hold reduced values there, so keep the old parts.
            foreach (var kind in _levers.Select(l => l.Kind).Where(k => !retained.Restored.Contains(k)))
            {
                snapshot.CopyPartFrom(retained, kind);
                _log.Write(Category, $"{kind}: kept the previous baseline (it was not restored)");
            }
        }

        return snapshot;
    }

    /// <summary>Capture one lever's part. False when it could not be read, or when the scheme read is one of PowerMode's own.</summary>
    private async Task<bool> CaptureLeverAsync(ILever lever, Snapshot snapshot, AppConfig config, CancellationToken cancellationToken)
    {
        if (!await _executor.CaptureAsync(lever, snapshot, config, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (lever.Kind == LeverKind.PowerScheme && snapshot.ActiveScheme is { } scheme && PowerSchemes.IsOwn(scheme))
        {
            // Not a baseline: Grid would never leave the PowerMode scheme. Grid uses the fallback scheme instead.
            _log.Write(Category, $"{lever.Kind}: a PowerMode scheme is active ({scheme}), not recorded as the baseline");
            snapshot.ActiveScheme = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Before a saving mode reduces anything: retry the parts whose capture failed, and capture names added to the
    /// close or service lists since the baseline was taken. True when the snapshot changed.
    /// </summary>
    private async Task<bool> CompleteSnapshotAsync(Snapshot snapshot, AppConfig config, IEnumerable<ILever> levers, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var lever in levers)
        {
            if (snapshot.Missing.Contains(lever.Kind))
            {
                // Into a scratch snapshot: a failed retry must not touch the baseline.
                var retry = new Snapshot();
                if (await CaptureLeverAsync(lever, retry, config, cancellationToken).ConfigureAwait(false))
                {
                    snapshot.CopyPartFrom(retry, lever.Kind);
                    changed = true;
                    _log.Write(Category, $"{lever.Kind}: baseline captured on retry");
                }
            }
            else if (HasUncoveredNames(lever.Kind, snapshot, config) && await CaptureLeverAsync(lever, snapshot, config, cancellationToken).ConfigureAwait(false))
            {
                changed = true;
                _log.Write(Category, $"{lever.Kind}: captured the names added to its list");
            }
        }

        return changed;
    }

    private static bool HasUncoveredNames(LeverKind kind, Snapshot snapshot, AppConfig config) => kind switch
    {
        LeverKind.Apps => config.AllCloseApps().Any(n => !TargetPlanner.Contains(snapshot.CoveredAppNames(), n)),
        LeverKind.Services => config.AllServices().Any(n => !TargetPlanner.Contains(snapshot.CoveredServiceNames(), n)),
        _ => false,
    };

    /// <summary>
    /// After Grid: every part that is back (or failed for good) is no longer owed. The baseline is cleared once nothing
    /// is owed; otherwise only the failed parts remain, so a retry (start, resume) touches only those.
    /// </summary>
    private void SettleGridRestore(SwitchOutcome outcome)
    {
        if (State.Snapshot is not { } snapshot)
        {
            return;
        }

        var settled = outcome.Results.Where(r => !r.Value.IsFailure || r.Value.Permanent).Select(r => r.Key).ToList();
        snapshot.Restored = [.. snapshot.Restored, .. settled];
        if (settled.Contains(LeverKind.Apps))
        {
            State.ClosedApps.Clear();
        }

        var owed = _levers.Select(l => l.Kind).Where(k => !snapshot.Restored.Contains(k)).ToList();
        if (owed.Count == 0)
        {
            State.Snapshot = null;
            State.ClosedApps.Clear();
            _log.Write(Category, "Baseline restored and cleared");
        }
        else
        {
            _log.Write(Category, $"Baseline kept for {string.Join(", ", owed)}: not restored yet");
        }
    }

    private ApplyContext Context(PowerModeKind mode, AppConfig config, Snapshot? snapshot) =>
        new(TargetPlanner.Plan(mode, config, snapshot, State.OptionsFor(mode)), State, _isElevated());

    private async Task<SwitchOutcome> ApplyAsync(PowerModeKind mode, AppConfig config, IReadOnlyCollection<LeverKind>? only, CancellationToken cancellationToken)
    {
        var selected = _levers.Where(l => only is null || only.Contains(l.Kind)).ToList();
        var kinds = selected.Select(l => l.Kind).ToHashSet();
        Progress?.Invoke(this, new SwitchProgress(SwitchPhase.Started, mode, kinds));

        if (mode != PowerModeKind.Grid && State.Snapshot is { } snapshot && await CompleteSnapshotAsync(snapshot, config, selected, cancellationToken).ConfigureAwait(false))
        {
            _store.Save(State);
        }

        var context = Context(mode, config, State.Snapshot);
        var blocked = Blocked(context.Target, State.Snapshot);
        var tasks = new Dictionary<LeverKind, Task<LeverResult>>();
        foreach (var lever in OrderByDependency(selected))
        {
            var predecessor = lever.RunsAfter is { } after && tasks.TryGetValue(after, out var task) ? task : null;
            tasks[lever.Kind] = blocked.Contains(lever.Kind)
                ? Task.FromResult(Block(lever.Kind, mode, kinds))
                : RunLeverAsync(lever, context, predecessor, kinds, cancellationToken);
        }

        await Task.WhenAll(tasks.Values).ConfigureAwait(false);
        var results = tasks.ToDictionary(t => t.Key, t => t.Value.Result);

        // Copy-on-write: the UI thread reads LastResults while a switch runs.
        var merged = new Dictionary<LeverKind, LeverResult>(State.LastResults);
        foreach (var (kind, result) in results)
        {
            merged[kind] = result;
        }

        State.LastResults = merged;

        State.LastApplied = _time.GetUtcNow();
        var outcome = new SwitchOutcome(mode, results);
        _log.Write(Category, $"{ModeNames.Display(mode)} applied, {outcome.FailedCount} lever(s) failed");
        Progress?.Invoke(this, new SwitchProgress(SwitchPhase.Completed, mode, kinds));
        return outcome;
    }

    /// <summary>The lever's baseline is missing: Grid could never undo a reduction now, so the lever is left as it is.</summary>
    private LeverResult Block(LeverKind kind, PowerModeKind mode, IReadOnlySet<LeverKind> kinds)
    {
        var result = LeverResult.Failed(LeverResult.BaselineNotCaptured);
        _log.Write(LeverExecutor.Category, $"{kind}: {result.Detail}");
        Progress?.Invoke(this, new SwitchProgress(SwitchPhase.LeverFinished, mode, kinds, kind, result));
        return result;
    }

    private async Task<LeverResult> RunLeverAsync(ILever lever, ApplyContext context, Task<LeverResult>? predecessor, IReadOnlySet<LeverKind> kinds, CancellationToken cancellationToken)
    {
        var predecessorChanged = false;
        if (predecessor is not null)
        {
            // E.g. a new display mode: the monitor re-syncs and does not answer DDC/CI for a moment.
            predecessorChanged = (await predecessor.ConfigureAwait(false)).Changed;
        }
        else
        {
            await Task.Yield();
        }

        var result = await _executor.ApplyAsync(lever, context, predecessorChanged, cancellationToken).ConfigureAwait(false);
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
