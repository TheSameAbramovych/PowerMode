using System.Globalization;

namespace PowerMode.Core.Input;

/// <summary>Modifier flags with the values RegisterHotKey expects (MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN).</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Windows = 0x8,
}

/// <summary>A parsed hotkey such as <c>Ctrl+Alt+1</c>: modifiers plus a Win32 virtual-key code.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private const uint VkDigit0 = 0x30;
    private const uint VkLetterA = 0x41;
    private const uint VkF1 = 0x70;
    private const uint VkNumpad0 = 0x60;
    private const int MaxFunctionKey = 24;
    private const string NumpadPrefix = "NUM";

    /// <summary>Parse "Ctrl+Alt+1", "Win+Shift+F5", "ctrl + alt + num3". At least one modifier is required.</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = ParseModifier(part);
            if (modifier == HotkeyModifiers.None || modifiers.HasFlag(modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        if (!TryParseKey(parts[^1], out var key))
        {
            return false;
        }

        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    private static HotkeyModifiers ParseModifier(string part) => part.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => HotkeyModifiers.Control,
        "ALT" => HotkeyModifiers.Alt,
        "SHIFT" => HotkeyModifiers.Shift,
        "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
        _ => HotkeyModifiers.None,
    };

    private static bool TryParseKey(string part, out uint key)
    {
        key = 0;
        var upper = part.ToUpperInvariant();
        if (upper.Length == 1 && char.IsAsciiDigit(upper[0]))
        {
            key = VkDigit0 + (uint)(upper[0] - '0');
            return true;
        }

        if (upper.Length == 1 && char.IsAsciiLetterUpper(upper[0]))
        {
            key = VkLetterA + (uint)(upper[0] - 'A');
            return true;
        }

        if (upper.Length == NumpadPrefix.Length + 1 && upper.StartsWith(NumpadPrefix, StringComparison.Ordinal) && char.IsAsciiDigit(upper[^1]))
        {
            key = VkNumpad0 + (uint)(upper[^1] - '0');
            return true;
        }

        if (upper.Length >= 2 && upper[0] == 'F'
            && int.TryParse(upper.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is >= 1 and <= MaxFunctionKey)
        {
            key = VkF1 + (uint)(number - 1);
            return true;
        }

        return false;
    }
}
