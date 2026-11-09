using PowerMode.App.Infrastructure;
using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;

namespace PowerMode.App.Levers;

/// <summary>Monitor brightness over DDC/CI (dxva2) on every physical monitor; Grid restores brightness and contrast.</summary>
internal sealed class DdcBrightnessLever : ILever
{
    private const string What = "DDC/CI";
    private const string NoMonitor = "No monitor answered over DDC/CI (asleep or DDC/CI off)";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    public LeverKind Kind => LeverKind.Brightness;

    /// <summary>A mode change makes the monitor resync; set brightness after it.</summary>
    public LeverKind? RunsAfter => LeverKind.DisplayMode;

    /// <summary>
    /// Record brightness and contrast. No monitor at all is a legitimately empty baseline; monitors that are there but
    /// do not answer (DDC/CI reads fail for a moment) are retried once and otherwise make the capture inconclusive.
    /// </summary>
    public async Task CaptureAsync(Snapshot snapshot, AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var (levels, enumerated) = await NativeCall.RunAsync(ReadAll, Timeout, What, cancellationToken).ConfigureAwait(false);
        if (levels.Count == 0 && enumerated > 0)
        {
            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            (levels, enumerated) = await NativeCall.RunAsync(ReadAll, Timeout, What, cancellationToken).ConfigureAwait(false);
            if (levels.Count == 0 && enumerated > 0)
            {
                throw new LeverException(NoMonitor);
            }
        }

        snapshot.Monitors = levels;
    }

    /// <summary>Current brightness and contrast of every monitor that answers over DDC/CI, and how many monitors there are.</summary>
    private static (List<MonitorLevel> Levels, int Enumerated) ReadAll()
    {
        using var monitors = DdcMonitors.Open();
        var levels = new List<MonitorLevel>();
        foreach (var monitor in monitors.Monitors)
        {
            if (DdcMonitors.Brightness(monitor) is { } brightness)
            {
                levels.Add(new MonitorLevel(monitor.Id, (int)brightness.Current, DdcMonitors.Contrast(monitor) is { } contrast ? (int)contrast.Current : null));
            }
        }

        return (levels, monitors.Monitors.Count);
    }

    public async Task<LeverPlan> PlanAsync(ApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target.Monitor is not { } target)
        {
            return LeverPlan.None;
        }

        return await NativeCall.RunAsync(() => Plan(target), Timeout, What, cancellationToken).ConfigureAwait(false);
    }

    private static LeverPlan Plan(MonitorTarget target)
    {
        using var monitors = DdcMonitors.Open();
        var actions = new List<LeverAction>();
        var answered = 0;
        foreach (var monitor in monitors.Monitors)
        {
            if (DdcMonitors.Brightness(monitor) is not { } brightness)
            {
                continue;
            }

            answered++;
            if (target.BrightnessPercent is { } percent)
            {
                var value = DdcMonitors.FromPercent(percent, brightness.Min, brightness.Max);
                if (value != brightness.Current)
                {
                    actions.Add(SetAction(monitor, "brightness", brightness.Current, value, isContrast: false));
                }

                continue;
            }

            if (target.Restore.FirstOrDefault(m => m.Id == monitor.Id) is not { } saved)
            {
                continue;
            }

            if ((uint)saved.Brightness != brightness.Current)
            {
                actions.Add(SetAction(monitor, "brightness", brightness.Current, (uint)saved.Brightness, isContrast: false));
            }

            if (saved.Contrast is { } contrast && DdcMonitors.Contrast(monitor) is { } currentContrast && currentContrast.Current != (uint)contrast)
            {
                actions.Add(SetAction(monitor, "contrast", currentContrast.Current, (uint)contrast, isContrast: true));
            }
        }

        return answered == 0 ? LeverPlan.Fail(NoMonitor) : LeverPlan.Of(actions);
    }

    private static LeverAction SetAction(DdcMonitor monitor, string what, uint from, uint to, bool isContrast) =>
        new($"{monitor.Description} ({monitor.Id}): {what} {from} -> {to}", ct => NativeCall.RunAsync(() => Set(monitor.Id, to, isContrast), Timeout, What, ct));

    private static bool Set(string id, uint value, bool isContrast)
    {
        using var monitors = DdcMonitors.Open();
        var monitor = monitors.Monitors.FirstOrDefault(m => m.Id == id) ?? throw new LeverException($"Monitor {id} is gone");
        var ok = isContrast ? Native.Dxva2.SetMonitorContrast(monitor.Handle, value) : Native.Dxva2.SetMonitorBrightness(monitor.Handle, value);
        return ok ? true : throw new LeverException($"{monitor.Description} refused the DDC/CI write");
    }
}
