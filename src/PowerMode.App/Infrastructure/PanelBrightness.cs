using System.Management;
using System.Runtime.InteropServices;

namespace PowerMode.App.Infrastructure;

/// <summary>
/// The built-in panel of a laptop, which has no DDC/CI: brightness through WMI (<c>root\wmi</c>
/// WmiMonitorBrightness / WmiMonitorBrightnessMethods). Absent on desktops; every call then answers "none".
/// </summary>
internal static class PanelBrightness
{
    /// <summary>The panel's monitor id in a snapshot (beside the DDC/CI ids <c>\\.\DISPLAY1#0</c>).</summary>
    public const string Id = "panel";

    private const string Scope = @"root\wmi";
    private const uint SetTimeoutSeconds = 1;

    /// <summary>Current brightness in percent; null when there is no such panel.</summary>
    public static int? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE");
            using var results = searcher.Get();
            foreach (var item in results)
            {
                using (item)
                {
                    return Convert.ToInt32(item["CurrentBrightness"], System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            // No panel (desktop) or WMI unavailable.
        }

        return null;
    }

    /// <summary>Set the panel's brightness; false when there is no panel or WMI refused.</summary>
    public static bool Set(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
            using var results = searcher.Get();
            foreach (ManagementObject method in results)
            {
                using (method)
                {
                    method.InvokeMethod("WmiSetBrightness", [SetTimeoutSeconds, (byte)Math.Clamp(percent, 0, 100)]);
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            // Fall through: not available.
        }

        return false;
    }
}
