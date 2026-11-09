using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using PowerMode.App.Native;
using PowerMode.Core.Logging;

namespace PowerMode.App.Infrastructure;

/// <summary>
/// The app's log: the daily file first; when it cannot be written, <c>%TEMP%\PowerMode\powermode-fallback.log</c>
/// and, once, a warning in the Windows Application event log (source <c>PowerMode</c>, when it exists or can be
/// created). Plus the start-up line that says which process this is and where it writes.
/// </summary>
internal static class LogSinks
{
    private const string EventSource = "PowerMode";
    private const string EventLogName = "Application";
    private const int BasicInformationClass = 0;
    private const int ParentIdSlot = 5;
    private const int BasicInformationSlots = 6;

    public static FileLog Create()
    {
        var log = new FileLog(AppPaths.LogDirectory, TimeProvider.System);
        log.Failed += (_, reason) => WriteEventLog(reason);
        return log;
    }

    /// <summary>Exe, pid, parent, elevation, working folder, data and log folders, Windows build, displays.</summary>
    public static string DescribeProcess() => string.Create(CultureInfo.InvariantCulture,
        $"Process: {AppPaths.ExecutablePath}, pid {Environment.ProcessId}, parent {ParentName()}, {(Elevation.IsElevated ? "elevated" : "not elevated")}, "
        + $"working folder {Environment.CurrentDirectory}, data {AppPaths.DataDirectory}, logs {AppPaths.LogDirectory}, "
        + $"Windows {Environment.OSVersion.Version}, {System.Windows.Forms.Screen.AllScreens.Length} display(s), {PackageNote()}");

    /// <summary>
    /// A process started from a packaged (MSIX) app runs inside that package: Windows redirects its writes to
    /// %LOCALAPPDATA% into the package's own LocalCache, so it sees other config, state and logs than a PowerMode
    /// started from Explorer.
    /// </summary>
    private static string PackageNote() =>
        ProcessInfo.TryGetAppUserModelId(Environment.ProcessId) is { } package
            ? $"inside package {package} (writes to %LOCALAPPDATA% are redirected to its LocalCache)"
            : "not packaged";

    private static void WriteEventLog(string message)
    {
        try
        {
            // Creating the source needs administrator rights once; without it there is nothing to write to.
            if (!EventLog.SourceExists(EventSource))
            {
                EventLog.CreateEventSource(EventSource, EventLogName);
            }

            EventLog.WriteEntry(EventSource, message, EventLogEntryType.Warning);
        }
        catch (Exception ex) when (ex is SecurityException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or Win32Exception or IOException)
        {
            // The fallback file has the lines; the event log is a best-effort extra.
        }
    }

    /// <summary>The parent process name (explorer, powershell, svchost for the scheduled task …), or why it is unknown.</summary>
    private static string ParentName()
    {
        using var self = ProcessInfo.Open(Environment.ProcessId, Kernel32.ProcessQueryLimitedInformation);
        var size = BasicInformationSlots * IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (self.IsInvalid || Ntdll.NtQueryInformationProcess(self, BasicInformationClass, buffer, size, out _) != 0)
            {
                return "unknown";
            }

            var parentId = (int)Marshal.ReadIntPtr(buffer, ParentIdSlot * IntPtr.Size);
            try
            {
                using var parent = Process.GetProcessById(parentId);
                return $"{parent.ProcessName} ({parentId})";
            }
            catch (ArgumentException)
            {
                return $"exited ({parentId})";
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
