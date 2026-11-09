using PowerMode.Core.Config;
using PowerMode.Core.Model;
using PowerMode.Core.State;

namespace PowerMode.Core.Levers;

/// <summary>One change a lever wants to make to reach its target. Executed by <see cref="LeverExecutor"/>.</summary>
public sealed record LeverAction(string Description, Func<CancellationToken, Task> Execute, bool RequiresElevation = false);

/// <summary>What a lever would do to reach the target from the current state.</summary>
public sealed record LeverPlan(IReadOnlyList<LeverAction> Actions, string? Unavailable = null, bool NotOwned = false, bool Permanent = false)
{
    /// <summary>The mode does not own this lever.</summary>
    public static LeverPlan None { get; } = new([], NotOwned: true);

    /// <summary>The lever is already at its target.</summary>
    public static LeverPlan Done { get; } = new([]);

    public static LeverPlan Of(IEnumerable<LeverAction> actions) => new([.. actions]);

    /// <summary>The lever cannot work on this machine (no NVML, Docker not installed, ...).</summary>
    public static LeverPlan Fail(string reason, bool permanent = false) => new([], reason, Permanent: permanent);
}

public sealed record ApplyContext(TargetState Target, PersistedState State, bool IsElevated);

/// <summary>
/// For a device that is briefly unreachable (a monitor re-syncing after a display-mode change). When the lever's
/// <see cref="ILever.RunsAfter"/> predecessor changed something in the same apply, wait <see cref="Settle"/> first;
/// then, while the plan reports the device unavailable (not permanently), plan again up to <see cref="Retries"/>
/// times, <see cref="Delay"/> apart. Every plan reads the device afresh.
/// </summary>
public sealed record LeverRetry(TimeSpan Settle, int Retries, TimeSpan Delay);

/// <summary>
/// A lever reads the current machine state and plans actions toward a <see cref="TargetState"/>.
/// Implementations live in the App; <see cref="LeverExecutor"/> guarantees logging, timing, elevation
/// gating and that nothing ever throws out of an apply.
/// </summary>
public interface ILever
{
    LeverKind Kind { get; }

    /// <summary>Another lever that must finish before this one starts (e.g. brightness after the display mode).</summary>
    LeverKind? RunsAfter => null;

    /// <summary>How to wait for a briefly unreachable device; null = plan once.</summary>
    LeverRetry? Retry => null;

    /// <summary>
    /// Record this lever's part of the baseline (read-only). Throw when the read failed or was inconclusive: the part
    /// is then marked missing and the lever is not reduced, rather than recording a wrong baseline.
    /// </summary>
    Task CaptureAsync(Snapshot snapshot, AppConfig config, CancellationToken cancellationToken);

    /// <summary>Compute the diff from the current state to the target (read-only).</summary>
    Task<LeverPlan> PlanAsync(ApplyContext context, CancellationToken cancellationToken);
}
