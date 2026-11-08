using PowerMode.Core.Config;
using PowerMode.Core.Model;
using PowerMode.Core.State;

namespace PowerMode.Core.Levers;

/// <summary>One change a lever wants to make to reach its target. Executed by <see cref="LeverExecutor"/>.</summary>
public sealed record LeverAction(string Description, Func<CancellationToken, Task> Execute, bool RequiresElevation = false);

/// <summary>What a lever would do to reach the target from the current state.</summary>
public sealed record LeverPlan(IReadOnlyList<LeverAction> Actions, string? Unavailable = null, bool NotOwned = false)
{
    /// <summary>The mode does not own this lever.</summary>
    public static LeverPlan None { get; } = new([], NotOwned: true);

    /// <summary>The lever is already at its target.</summary>
    public static LeverPlan Done { get; } = new([]);

    public static LeverPlan Of(IEnumerable<LeverAction> actions) => new([.. actions]);

    /// <summary>The lever cannot work on this machine (no NVML, Docker not installed, ...).</summary>
    public static LeverPlan Fail(string reason) => new([], reason);
}

public sealed record ApplyContext(TargetState Target, PersistedState State, bool IsElevated);

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

    /// <summary>Record this lever's part of the baseline (read-only).</summary>
    Task CaptureAsync(Snapshot snapshot, AppConfig config, CancellationToken cancellationToken);

    /// <summary>Compute the diff from the current state to the target (read-only).</summary>
    Task<LeverPlan> PlanAsync(ApplyContext context, CancellationToken cancellationToken);
}
