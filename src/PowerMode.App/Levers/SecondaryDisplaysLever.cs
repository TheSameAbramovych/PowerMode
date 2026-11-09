using PowerMode.App.Infrastructure;
using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;

namespace PowerMode.App.Levers;

/// <summary>
/// Secondary displays off (Windows topology "primary only") and back on (the topology saved when leaving Grid, or
/// the desktop extended to every display when Windows refuses it). Skipped with one display.
/// </summary>
internal sealed class SecondaryDisplaysLever : ILever
{
    private const string What = "Display topology";
    private const string OneDisplay = "One display connected";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public LeverKind Kind => LeverKind.SecondaryDisplays;

    public async Task CaptureAsync(Snapshot snapshot, AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Topology = await NativeCall.RunAsync(Displays.Capture, Timeout, What, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LeverPlan> PlanAsync(ApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target.Topology is not { } target)
        {
            return LeverPlan.None;
        }

        var displays = await NativeCall.RunAsync(Displays.All, Timeout, What, cancellationToken).ConfigureAwait(false);
        return DisplayDecisions.Decide(target, displays.Count, displays.Count(d => d.Active)) switch
        {
            TopologyAction.SkipOneDisplay => LeverPlan.Skip(OneDisplay),
            TopologyAction.PrimaryOnly => LeverPlan.Of([new LeverAction(
                $"Turn off {string.Join(", ", displays.Where(d => d.Active && !d.Primary).Select(d => d.Name))}",
                ct => NativeCall.RunAsync(Displays.PrimaryOnly, Timeout, What, ct))]),
            TopologyAction.Restore => LeverPlan.Of([new LeverAction(
                $"Turn on {string.Join(", ", displays.Where(d => !d.Active).Select(d => d.Name))}",
                ct => NativeCall.RunAsync(() => Displays.Restore(target.Restore!), Timeout, What, ct))]),
            _ => LeverPlan.Done,
        };
    }
}
