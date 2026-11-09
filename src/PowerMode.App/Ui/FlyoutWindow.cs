using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PowerMode.App.Native;
using PowerMode.Core;
using PowerMode.Core.Config;

namespace PowerMode.App.Ui;

/// <summary>
/// Borderless 380 px flyout, height to content (max 600), DWM rounded corners + 1 px border, anchored to a
/// corner of the work area. Hides on deactivation or Esc; 1/2/3 switch modes.
/// </summary>
internal sealed class FlyoutWindow : Window
{
    public const double MaxFlyoutHeight = 600;

    private readonly Func<FlyoutConfig> _config;
    private IntPtr _handle;

    public FlyoutWindow(FlyoutView view, Func<FlyoutConfig> config)
    {
        _config = config;
        View = view;
        Content = view;
        Title = "PowerMode";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = view.Width;
        MaxHeight = MaxFlyoutHeight;
        ShowActivated = true;
        SetResourceReference(BackgroundProperty, Palette.Background);
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;

            // Software rendering: PowerMode itself changes the display mode (200 Hz <-> 60 Hz). A Direct3D render
            // target first created after such a change presented nothing at all (layout and UI tree complete, only
            // the window background on screen). The flyout is small and static; GDI presentation does not depend on
            // the Direct3D device surviving the mode change.
            if (HwndSource.FromHwnd(_handle)?.CompositionTarget is { } target)
            {
                target.RenderMode = RenderMode.SoftwareOnly;
            }

            ApplyFrame();
        };
        SizeChanged += (_, _) => Reposition();
        Deactivated += (_, _) => HideFlyout();
    }

    public FlyoutView View { get; }

    /// <summary>When the flyout was last hidden; a tray click right after that must not reopen it.</summary>
    public DateTime HiddenAt { get; private set; }

    public event EventHandler<PowerModeKind>? ModeKey;

    public void ShowFlyout()
    {
        Show();

        // Safeguard: lay out and paint the content now, so a shown flyout is never left with only its background.
        UpdateLayout();
        View.InvalidateVisual();
        Reposition();
        Activate();
        User32.SetForegroundWindow(_handle);
        Focus();
    }

    public void HideFlyout()
    {
        if (IsVisible)
        {
            Hide();
            HiddenAt = DateTime.UtcNow;
        }
    }

    /// <summary>Rounded corners and a border in the palette colour (re-run when the theme changes).</summary>
    public void ApplyFrame()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        var corner = Dwmapi.CornerRound;
        _ = Dwmapi.DwmSetWindowAttribute(_handle, Dwmapi.WindowCornerPreference, ref corner, sizeof(int));
        if (TryFindResource(Palette.Border) is SolidColorBrush brush)
        {
            var colorRef = brush.Color.R | (brush.Color.G << 8) | (brush.Color.B << 16);
            _ = Dwmapi.DwmSetWindowAttribute(_handle, Dwmapi.BorderColor, ref colorRef, sizeof(int));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var index = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            _ => -1,
        };
        if (e.Key == Key.Escape)
        {
            HideFlyout();
            e.Handled = true;
        }
        else if (index >= 0)
        {
            ModeKey?.Invoke(this, ModeNames.All[index]);
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        var config = _config();
        var height = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
        Left = config.Anchor is FlyoutAnchor.BottomLeft or FlyoutAnchor.TopLeft ? area.Left + config.Margin : area.Right - Width - config.Margin;
        Top = config.Anchor is FlyoutAnchor.TopLeft or FlyoutAnchor.TopRight ? area.Top + config.Margin : area.Bottom - height - config.Margin;
    }
}
