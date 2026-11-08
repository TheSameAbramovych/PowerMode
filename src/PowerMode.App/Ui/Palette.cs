using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using PowerMode.Core;

namespace PowerMode.App.Ui;

/// <summary>
/// Colours for both themes (text contrast &gt;= 4.5:1 against the flyout background), published as brushes in
/// application resources under the <c>Pm*</c> keys so the XAML can use DynamicResource.
/// </summary>
internal static class Palette
{
    public const string Background = "PmBackground";
    public const string Border = "PmBorder";
    public const string Divider = "PmDivider";
    public const string Text = "PmText";
    public const string Muted = "PmMuted";
    public const string Hover = "PmHover";
    public const string Pressed = "PmPressed";
    public const string Ok = "PmOk";
    public const string Warning = "PmWarning";
    public const string WarningFill = "PmWarningFill";
    public const string Accent = "PmAccent";
    public const string OnAccent = "PmOnAccent";
    public const string SwitchOff = "PmSwitchOff";

    /// <summary>Tray icon colours, fixed by the spec (they sit on the taskbar, not the flyout).</summary>
    public static readonly Color TrayStation = Color.FromRgb(0xF2, 0xB8, 0x3C);
    public static readonly Color TrayReserve = Color.FromRgb(0xFF, 0x6B, 0x6B);

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool AppsUseLightTheme() => ReadFlag("AppsUseLightTheme");

    public static bool TaskbarUsesLightTheme() => ReadFlag("SystemUsesLightTheme");

    private static bool ReadFlag(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(name) is int value && value != 0;
    }

    public static void Apply(ResourceDictionary resources, bool light)
    {
        Set(resources, Background, light ? "#F9F9F9" : "#2B2B2B");
        Set(resources, Border, light ? "#D1D1D1" : "#4A4A4A");
        Set(resources, Divider, light ? "#E6E6E6" : "#3A3A3A");
        Set(resources, Text, light ? "#1A1A1A" : "#FFFFFF");
        Set(resources, Muted, light ? "#5C5C5C" : "#C2C2C2");
        Set(resources, Hover, light ? "#EDEDED" : "#363636");
        Set(resources, Pressed, light ? "#E2E2E2" : "#3E3E3E");
        Set(resources, Ok, light ? "#0E7A0D" : "#6CCB5F");
        Set(resources, Warning, light ? "#8F5A00" : "#F2B83C");
        Set(resources, WarningFill, light ? "#FFF4CE" : "#433519");
        Set(resources, Accent, light ? "#005FB8" : "#60CDFF");
        Set(resources, OnAccent, light ? "#FFFFFF" : "#003A57");
        Set(resources, SwitchOff, light ? "#8A8A8A" : "#9E9E9E");
        foreach (var mode in ModeNames.All)
        {
            Set(resources, ModeKey(mode), ModeHex(mode, light));
        }
    }

    public static string ModeKey(PowerModeKind mode) => "PmMode" + ModeNames.Display(mode);

    /// <summary>Mode colour in the flyout. Station/Reserve use the spec's tray colours, darkened in light theme for contrast.</summary>
    public static string ModeHex(PowerModeKind mode, bool light) => mode switch
    {
        PowerModeKind.Grid => light ? "#005FB8" : "#60CDFF",
        PowerModeKind.Station => light ? "#8F5A00" : "#F2B83C",
        PowerModeKind.Reserve => light ? "#C42B1C" : "#FF6B6B",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public static Color ModeColor(PowerModeKind mode, bool light) => (Color)ColorConverter.ConvertFromString(ModeHex(mode, light));

    private static void Set(ResourceDictionary resources, string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        resources[key] = brush;
    }
}
