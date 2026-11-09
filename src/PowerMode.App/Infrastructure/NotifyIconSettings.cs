using System.Runtime.InteropServices;
using Microsoft.Win32;
using PowerMode.App.Native;
using PowerMode.Core.Presentation;

namespace PowerMode.App.Infrastructure;

/// <summary>
/// Windows 11 (22H2+) per-icon tray settings under <c>HKCU\Control Panel\NotifyIconSettings\&lt;id&gt;</c>:
/// <c>ExecutablePath</c> and <c>IsPromoted</c> (1 = on the taskbar). Read and written for our own entries only;
/// which ones is decided by <see cref="TrayPromotion"/>.
/// </summary>
internal static class NotifyIconSettings
{
    private const string KeyPath = @"Control Panel\NotifyIconSettings";
    private const string ExecutablePathValue = "ExecutablePath";
    private const string IsPromotedValue = "IsPromoted";

    /// <summary>Every icon entry; null when this Windows has no such key.</summary>
    public static List<NotifyIconEntry>? Read()
    {
        using var root = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (root is null)
        {
            return null;
        }

        var entries = new List<NotifyIconEntry>();
        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key is not null)
            {
                entries.Add(new NotifyIconEntry(name, key.GetValue(ExecutablePathValue) as string, key.GetValue(IsPromotedValue) as int?));
            }
        }

        return entries;
    }

    public static void Promote(IEnumerable<string> keys)
    {
        foreach (var name in keys)
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{KeyPath}\{name}", writable: true);
            key?.SetValue(IsPromotedValue, 1, RegistryValueKind.DWord);
        }
    }

    /// <summary>The path of a known folder (the GUID prefix Windows writes into <c>ExecutablePath</c>), or null.</summary>
    public static string? KnownFolder(Guid id)
    {
        if (Shell32.SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var path) != Shell32.SOk)
        {
            Marshal.FreeCoTaskMem(path);
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }
}
