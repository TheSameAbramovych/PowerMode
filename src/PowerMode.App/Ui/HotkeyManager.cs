using System.Windows.Interop;
using PowerMode.App.Native;
using PowerMode.Core;
using PowerMode.Core.Config;
using PowerMode.Core.Input;
using PowerMode.Core.Logging;

namespace PowerMode.App.Ui;

/// <summary>Global hotkeys (RegisterHotKey) on a message-only window; one per mode, from the config strings.</summary>
internal sealed class HotkeyManager : IDisposable
{
    private const string Category = "hotkey";
    private static readonly IntPtr MessageOnlyParent = new(-3);

    private readonly HwndSource _window;
    private readonly ILog _log;
    private readonly Dictionary<int, PowerModeKind> _registered = [];

    public HotkeyManager(ILog log)
    {
        _log = log;
        _window = new HwndSource(new HwndSourceParameters("PowerModeHotkeys") { ParentWindow = MessageOnlyParent, WindowStyle = 0 });
        _window.AddHook(WndProc);
    }

    public event EventHandler<PowerModeKind>? Pressed;

    public void Register(HotkeyConfig hotkeys)
    {
        Unregister();
        var id = 1;
        foreach (var mode in ModeNames.All)
        {
            var text = hotkeys.For(mode);
            if (!Hotkey.TryParse(text, out var hotkey))
            {
                _log.Write(Category, $"{ModeNames.Display(mode)}: no hotkey (\"{text}\")");
                continue;
            }

            if (User32.RegisterHotKey(_window.Handle, id, (uint)hotkey.Modifiers, hotkey.VirtualKey))
            {
                _registered[id] = mode;
            }
            else
            {
                _log.Write(Category, $"{text} is taken by another app ({ModeNames.Display(mode)} has no hotkey)");
            }

            id++;
        }
    }

    private void Unregister()
    {
        foreach (var id in _registered.Keys)
        {
            User32.UnregisterHotKey(_window.Handle, id);
        }

        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == User32.WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out var mode))
        {
            handled = true;
            Pressed?.Invoke(this, mode);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}
