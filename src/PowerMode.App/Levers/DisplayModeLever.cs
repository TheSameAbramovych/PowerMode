using PowerMode.App.Infrastructure;
using PowerMode.App.Native;
using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;

namespace PowerMode.App.Levers;

/// <summary>
/// Refresh rate of every display attached to the desktop. Saving modes pick the target rate at the current
/// resolution and colour depth when the display offers it; Grid restores the captured mode.
/// </summary>
internal sealed class DisplayModeLever : ILever
{
    private const string What = "Display mode change";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public LeverKind Kind => LeverKind.DisplayMode;

    /// <summary>Turning displays off or back on changes their modes; set the rate after it.</summary>
    public LeverKind? RunsAfter => LeverKind.SecondaryDisplays;

    public Task CaptureAsync(Snapshot snapshot, AppConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Displays = [.. CurrentModes()];
        return Task.CompletedTask;
    }

    public Task<LeverPlan> PlanAsync(ApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target.Refresh is not { } target)
        {
            return Task.FromResult(LeverPlan.None);
        }

        var current = CurrentModes().ToList();
        if (current.Count == 0)
        {
            return Task.FromResult(LeverPlan.Fail("No display found"));
        }

        var actions = new List<LeverAction>();
        foreach (var display in current)
        {
            // Within a hertz is already there (60 Hz reads back as 59 Hz); changing it again would blank the screen.
            // A display without the rate gets its lowest rate from 50 Hz; one display never fails the whole lever.
            var note = string.Empty;
            DisplayModeInfo? wanted;
            if (target.RefreshHz is { } hz)
            {
                var choice = RefreshRates.Same(display.RefreshHz, hz) ? null : DisplayDecisions.ChooseRefresh(OfferedRates(display), hz);
                wanted = choice is { } c && !RefreshRates.Same(display.RefreshHz, c.Hz) ? display with { RefreshHz = c.Hz } : null;
                note = choice is { Fallback: true } ? $" (no {hz} Hz at this resolution; its lowest rate from {DisplayDecisions.MinFallbackHz} Hz)" : string.Empty;
            }
            else
            {
                wanted = target.Restore.FirstOrDefault(d => string.Equals(d.Device, display.Device, StringComparison.OrdinalIgnoreCase));
            }

            if (wanted is not null && !wanted.Matches(display))
            {
                actions.Add(new LeverAction(
                    $"{display.Device}: {Describe(display)} -> {Describe(wanted)}{note}",
                    ct => NativeCall.RunAsync(() => Change(wanted), Timeout, What, ct)));
            }
        }

        return Task.FromResult(LeverPlan.Of(actions));
    }

    /// <summary>Current mode of every display attached to the desktop (device names from EnumDisplayDevices).</summary>
    public static IEnumerable<DisplayModeInfo> CurrentModes()
    {
        foreach (var device in AttachedDevices())
        {
            var mode = DevMode.Create();
            if (User32.EnumDisplaySettingsEx(device, User32.EnumCurrentSettings, ref mode, 0))
            {
                yield return new DisplayModeInfo(device, mode.PelsWidth, mode.PelsHeight, mode.DisplayFrequency, mode.BitsPerPel);
            }
        }
    }

    private static IEnumerable<string> AttachedDevices()
    {
        for (uint i = 0; ; i++)
        {
            var device = DisplayDevice.Create();
            if (!User32.EnumDisplayDevices(null, i, ref device, 0))
            {
                yield break;
            }

            if ((device.StateFlags & User32.DisplayDeviceAttachedToDesktop) != 0)
            {
                yield return device.DeviceName;
            }
        }
    }

    /// <summary>Refresh rates the display offers at its current resolution and colour depth.</summary>
    public static List<int> OfferedRates(DisplayModeInfo display)
    {
        var rates = new List<int>();
        var mode = DevMode.Create();
        for (var i = 0; User32.EnumDisplaySettingsEx(display.Device, i, ref mode, 0); i++)
        {
            if (mode.PelsWidth == display.Width && mode.PelsHeight == display.Height && mode.BitsPerPel == display.BitsPerPixel)
            {
                rates.Add(mode.DisplayFrequency);
            }
        }

        return rates;
    }

    private static bool Change(DisplayModeInfo wanted)
    {
        var mode = DevMode.Create();
        if (!User32.EnumDisplaySettingsEx(wanted.Device, User32.EnumCurrentSettings, ref mode, 0))
        {
            throw new LeverException($"{wanted.Device} is gone");
        }

        mode.PelsWidth = wanted.Width;
        mode.PelsHeight = wanted.Height;
        mode.BitsPerPel = wanted.BitsPerPixel;
        mode.DisplayFrequency = wanted.RefreshHz;
        mode.Fields = User32.DmPelsWidth | User32.DmPelsHeight | User32.DmBitsPerPel | User32.DmDisplayFrequency;
        var code = User32.ChangeDisplaySettingsEx(wanted.Device, ref mode, IntPtr.Zero, User32.CdsUpdateRegistry, IntPtr.Zero);
        if (code is not (User32.DispChangeSuccessful or User32.DispChangeRestart))
        {
            throw new LeverException($"{wanted.Device} refused {Describe(wanted)} (code {code})");
        }

        return true;
    }

    private static string Describe(DisplayModeInfo mode) => $"{mode.Width}x{mode.Height} @ {mode.RefreshHz} Hz";
}
