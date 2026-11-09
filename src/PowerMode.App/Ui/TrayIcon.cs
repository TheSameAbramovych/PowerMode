using System.Windows.Forms;
using PowerMode.App.Native;
using PowerMode.Core;

namespace PowerMode.App.Ui;

/// <summary>The notification-area icon: glyph per mode, spinner dot while applying, tooltip, menu and balloons.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const int BalloonMilliseconds = 4000;
    private const int MaxTooltipLength = 127;

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly Dictionary<PowerModeKind, ToolStripMenuItem> _modeItems = [];
    private readonly ToolStripMenuItem _autostartItem;
    private IntPtr _iconHandle;
    private (PowerModeKind, int, bool, int?)? _iconKey;

    public TrayIcon()
    {
        foreach (var mode in ModeNames.All)
        {
            var item = new ToolStripMenuItem(ModeNames.Display(mode)) { CheckOnClick = false };
            item.Click += (_, _) => ModeRequested?.Invoke(this, mode);
            _modeItems[mode] = item;
            _menu.Items.Add(item);
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Open", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        _autostartItem = new ToolStripMenuItem("Start with Windows");
        _autostartItem.Click += (_, _) => AutostartToggled?.Invoke(this, !_autostartItem.Checked);
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add("Open config", null, (_, _) => ConfigRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add("Create desktop shortcut", null, (_, _) => ShortcutRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Switch to Grid and quit", null, (_, _) => QuitRequested?.Invoke(this, true));
        _menu.Items.Add("Quit (keep mode)", null, (_, _) => QuitRequested?.Invoke(this, false));

        _icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = false };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                LeftClicked?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public event EventHandler<PowerModeKind>? ModeRequested;

    public event EventHandler? OpenRequested;

    public event EventHandler<bool>? AutostartToggled;

    public event EventHandler? ConfigRequested;

    public event EventHandler? ShortcutRequested;

    /// <summary>True = switch to Grid first.</summary>
    public event EventHandler<bool>? QuitRequested;

    public event EventHandler? LeftClicked;

    public void Show() => _icon.Visible = true;

    /// <summary>Remove and add the icon again, so the taskbar reads its settings (promotion) anew.</summary>
    public void Readd()
    {
        _icon.Visible = false;
        _icon.Visible = true;
    }

    public void Update(PowerModeKind mode, bool switching, int? spinnerFrame, bool autostart, string tooltip)
    {
        foreach (var (itemMode, item) in _modeItems)
        {
            item.Checked = itemMode == mode;
            item.Enabled = !switching;
        }

        _autostartItem.Checked = autostart;
        _icon.Text = tooltip.Length > MaxTooltipLength ? tooltip[..MaxTooltipLength] : tooltip;

        var size = SystemInformation.SmallIconSize.Width;
        var key = (mode, size, Palette.TaskbarUsesLightTheme(), spinnerFrame);
        if (key == _iconKey)
        {
            return;
        }

        _iconKey = key;
        var bitmap = IconRenderer.Render(mode, size, key.Item3, spinnerFrame);
        var previous = _iconHandle;
        var oldIcon = _icon.Icon;
        _icon.Icon = IconRenderer.ToIcon(bitmap, out _iconHandle);
        oldIcon?.Dispose();
        if (previous != IntPtr.Zero)
        {
            User32.DestroyIcon(previous);
        }
    }

    public void Toast(string title, string text) => _icon.ShowBalloonTip(BalloonMilliseconds, title, text, ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
        _menu.Dispose();
        if (_iconHandle != IntPtr.Zero)
        {
            User32.DestroyIcon(_iconHandle);
        }
    }
}
