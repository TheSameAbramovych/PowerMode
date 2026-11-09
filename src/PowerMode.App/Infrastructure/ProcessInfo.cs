using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PowerMode.App.Native;

namespace PowerMode.App.Infrastructure;

/// <summary>Reads the image path and command line of another process (no WMI; needs only limited query rights).</summary>
internal static class ProcessInfo
{
    private const int UnicodeStringBufferOffset = 8;
    private const int MaxPath = 32768;

    public static SafeProcessHandle Open(int processId, uint access) => Kernel32.OpenProcess(access, false, processId);

    public static string? TryGetPath(int processId)
    {
        using var handle = Open(processId, Kernel32.ProcessQueryLimitedInformation);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[MaxPath];
        var size = buffer.Length;
        return Kernel32.QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    /// <summary>
    /// The AppUserModelID of a packaged (MSIX) process, e.g. <c>OpenAI.Codex_2p2nqsd0c76g0!App</c>; null for an
    /// unpackaged process (APPMODEL_ERROR_NO_APPLICATION) or when it cannot be read.
    /// </summary>
    public static string? TryGetAppUserModelId(int processId)
    {
        using var handle = Open(processId, Kernel32.ProcessQueryLimitedInformation);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[Kernel32.AppUserModelIdMaxLength];
        var length = (uint)buffer.Length;
        return Kernel32.GetApplicationUserModelId(handle, ref length, buffer) == Kernel32.ErrorSuccess && length > 1
            ? new string(buffer, 0, (int)length).TrimEnd('\0')
            : null;
    }

    public static string? TryGetCommandLine(int processId)
    {
        using var handle = Open(processId, Kernel32.ProcessQueryLimitedInformation);
        if (handle.IsInvalid)
        {
            return null;
        }

        _ = Ntdll.NtQueryInformationProcess(handle, Ntdll.ProcessCommandLineInformation, IntPtr.Zero, 0, out var length);
        if (length <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (Ntdll.NtQueryInformationProcess(handle, Ntdll.ProcessCommandLineInformation, buffer, length, out _) != 0)
            {
                return null;
            }

            var byteLength = (ushort)Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, UnicodeStringBufferOffset);
            return Marshal.PtrToStringUni(text, byteLength / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The arguments part of a command line (everything after the quoted or bare executable).</summary>
    public static string ArgumentsOf(string commandLine)
    {
        var text = commandLine.TrimStart();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end < 0 ? string.Empty : text[(end + 1)..].Trim();
        }

        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? string.Empty : text[(space + 1)..].Trim();
    }

    public static DateTimeOffset? TryGetStart(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Running processes with this name (without .exe). The caller disposes them.</summary>
    public static Process[] ByName(string name)
    {
        try
        {
            return Process.GetProcessesByName(name);
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Processes of this user session for the Apps settings page, one per name: whether any of them has a visible
    /// top-level window, and whether it is a system process (image under the Windows folder, or unreadable).
    /// </summary>
    public static List<Core.Settings.RunningApp> RunningApps()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var self = Environment.ProcessId;
        int session;
        using (var current = Process.GetCurrentProcess())
        {
            session = current.SessionId;
        }

        var processes = Process.GetProcesses();
        try
        {
            return [.. processes
                .Where(p => SessionOf(p) == session && p.Id != self)
                .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var path = g.Select(p => TryGetPath(p.Id)).FirstOrDefault(p => p is not null);
                    var isSystem = path is null || path.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    return new Core.Settings.RunningApp(g.Key, g.Any(HasWindow), isSystem);
                })];
        }
        finally
        {
            DisposeAll(processes);
        }
    }

    private static int SessionOf(Process process)
    {
        try
        {
            return process.SessionId;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return -1;
        }
    }

    private static bool HasWindow(Process process)
    {
        try
        {
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static void DisposeAll(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }
}
