using System.Diagnostics;
using System.Runtime.InteropServices;
using PowerMode.Core.Levers;
using PowerMode.Core.Logging;

namespace PowerMode.App.Infrastructure;

/// <summary>
/// Starts apps as the normal (non-elevated) user. When PowerMode runs elevated (autostart task with highest
/// privileges), a plain Process.Start would relaunch Steam/OneDrive elevated; instead the desktop shell
/// (explorer) is asked to start them via IShellDispatch2.ShellExecute.
/// </summary>
internal sealed class UserLauncher
{
    private const string Category = "launch";
    private const string OpenVerb = "open";
    private const int ShowNormal = 1;
    private const int ShowHidden = 0;
    private const int ShellAttempts = 3;
    private static readonly TimeSpan ShellRetryDelay = TimeSpan.FromSeconds(1);

    private readonly ILog _log;

    public UserLauncher(ILog log) => _log = log;

    /// <summary>
    /// Start <paramref name="path"/> (an exe, or a shell URI such as <c>shell:AppsFolder\&lt;AUMID&gt;</c>) as the normal
    /// user, in <paramref name="workingDirectory"/> (default: the exe's folder). Throws <see cref="LeverException"/> when
    /// PowerMode is elevated and the desktop shell cannot start it: it never falls back to an elevated start.
    /// </summary>
    public void Launch(string path, string arguments, bool hidden, string? workingDirectory = null)
    {
        var directory = workingDirectory ?? Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        if (Elevation.IsElevated)
        {
            LaunchThroughShell(path, arguments, directory, hidden);
            return;
        }

        var info = new ProcessStartInfo(path, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = directory,
            WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
        };
        using var process = Process.Start(info);
    }

    /// <summary>Explorer may be restarting (right after a display mode change) or not registered yet at logon: retry briefly.</summary>
    private void LaunchThroughShell(string path, string arguments, string directory, bool hidden)
    {
        var name = Path.GetFileName(path);
        Exception? last = null;
        for (var attempt = 1; attempt <= ShellAttempts; attempt++)
        {
            try
            {
                ShellExecuteAsDesktopUser(path, arguments, directory, hidden ? ShowHidden : ShowNormal);
                return;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                last = ex;
                _log.Write(Category, $"Desktop shell launch of {name} failed (attempt {attempt} of {ShellAttempts}): {ex.Message}");
                if (attempt < ShellAttempts)
                {
                    Thread.Sleep(ShellRetryDelay);
                }
            }
        }

        // Process.Start from here would inherit the elevated token: fail loudly instead.
        _log.Error(Category, $"Cannot start {name} as the normal user", last!);
        throw new LeverException($"Could not start {name} as the normal user: the desktop shell (Explorer) is unavailable");
    }

    private static void ShellExecuteAsDesktopUser(string path, string arguments, string directory, int show)
    {
        var shellWindowsType = Type.GetTypeFromCLSID(ShellGuids.ShellWindows, throwOnError: true)!;
        dynamic shellWindows = Activator.CreateInstance(shellWindowsType)!;
        object? desktop = null;
        IShellBrowser? browser = null;
        IShellView? view = null;
        try
        {
            object location = ShellGuids.CsidlDesktop;
            object root = Missing;
            int windowHandle;
            desktop = (object?)shellWindows.FindWindowSW(ref location, ref root, ShellGuids.SwcDesktop, out windowHandle, ShellGuids.SwfoNeedDispatch)
                ?? throw new InvalidOperationException("No desktop shell window is registered");
            var provider = (IServiceProvider)desktop;
            var browserGuid = ShellGuids.TopLevelBrowser;
            var browserIid = typeof(IShellBrowser).GUID;
            browser = (IShellBrowser?)provider.QueryService(ref browserGuid, ref browserIid)
                ?? throw new InvalidOperationException("The desktop has no shell browser");
            view = browser.QueryActiveShellView() ?? throw new InvalidOperationException("The desktop has no active shell view");
            var dispatchIid = ShellGuids.Dispatch;
            dynamic? folderView = view.GetItemObject(ShellGuids.SvgioBackground, ref dispatchIid);
            dynamic? shell = (folderView ?? throw new InvalidOperationException("The desktop view has no folder view")).Application;
            if (shell is null)
            {
                throw new InvalidOperationException("The desktop folder view has no shell application");
            }

            shell.ShellExecute(path, arguments, directory, OpenVerb, show);
        }
        finally
        {
            // Released on every path, so a failure halfway down the chain leaks nothing.
            Release(view);
            Release(browser);
            Release(desktop);
            Marshal.FinalReleaseComObject(shellWindows);
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }

    private static object Missing => System.Reflection.Missing.Value;

    private static class ShellGuids
    {
        public static readonly Guid ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        public static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
        public static readonly Guid Dispatch = new("00020400-0000-0000-C000-000000000046");
        public const int CsidlDesktop = 0;
        public const int SwcDesktop = 8;
        public const int SwfoNeedDispatch = 1;
        public const uint SvgioBackground = 0;
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        // IOleWindow and the IShellBrowser methods before QueryActiveShellView; only their vtable slots matter.
        void GetWindow();
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();

        IShellView QueryActiveShellView();
    }

    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void TranslateAccelerator();
        void EnableModeless();
        void UIActivate();
        void Refresh();
        void CreateViewWindow();
        void DestroyViewWindow();
        void GetCurrentInfo();
        void AddPropertySheetPages();
        void SaveViewState();
        void SelectItem();

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object GetItemObject(uint item, ref Guid riid);
    }
}
