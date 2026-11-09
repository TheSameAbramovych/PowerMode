using PowerMode.Core.Config;

namespace PowerMode.Core.Presentation;

/// <summary>A connected display as the cheap topology read sees it: name, GDI device (null while it is off), built-in panel or not, refresh rate.</summary>
public sealed record DisplayReading(string Name, string? Device, bool Internal, int? RefreshHz);

/// <summary>
/// The monitor list of the live column and the power estimate: the display topology and refresh rates, read on every
/// sample, merged with the brightness read more rarely over DDC/CI (and WMI for a built-in panel).
/// </summary>
public static class LiveMonitors
{
    /// <summary>
    /// One <see cref="MonitorLive"/> per display, in the given order. Brightness, per display: the last DDC/CI value
    /// read for its device; for a built-in panel without DDC/CI the panel's WMI value (never given to an external
    /// monitor); before the first brightness read, <paramref name="fallbackPercent"/>; otherwise unknown (null). A
    /// display that newly turns on keeps null until the next read.
    /// </summary>
    /// <param name="displays">Primary first.</param>
    /// <param name="ddc">DDC/CI brightness per GDI device from the last read; null before the first read.</param>
    /// <param name="panel">The built-in panel's brightness through WMI from the last read; null without one.</param>
    /// <param name="fallbackPercent">What the brightness is assumed to be until it has been read (see <see cref="Fallback"/>).</param>
    public static List<MonitorLive> Merge(IReadOnlyList<DisplayReading> displays, IReadOnlyDictionary<string, int>? ddc, int? panel, int? fallbackPercent)
    {
        ArgumentNullException.ThrowIfNull(displays);
        var monitors = new List<MonitorLive>(displays.Count);
        foreach (var display in displays)
        {
            if (display.Device is not { } device)
            {
                monitors.Add(new MonitorLive(display.Name, null, null, Active: false));
                continue;
            }

            int? percent = ddc is null ? fallbackPercent
                : ddc.TryGetValue(device, out var read) ? read
                : display.Internal ? panel
                : null;
            monitors.Add(new MonitorLive(display.Name, percent, display.RefreshHz));
        }

        return monitors;
    }

    /// <summary>
    /// The brightness assumed before it has been read: the mode's configured brightness in Station or Reserve; null in
    /// Grid, where it is the owner's own (the estimate then uses its unknown-brightness value).
    /// </summary>
    public static int? Fallback(AppConfig config, PowerModeKind mode)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Modes.For(mode)?.BrightnessPercent;
    }
}
