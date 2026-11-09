using System.Globalization;
using PowerMode.Core.Config;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;
using PowerMode.Core.Presentation;

namespace PowerMode.Core.Settings;

/// <summary>One setting of the Levers page; every value is an <c>int?</c> (on/off as 1/0, null = leave alone).</summary>
public enum LeverSetting
{
    CpuMaxPercent,
    Boost,
    GpuLimitW,
    BrightnessPercent,
    RefreshHz,
    SecondaryDisplays,
    DisplayOffMinutes,
    HibernateMinutes,
    Docker,
    WallpaperEngine,
}

public sealed record SettingOption(int? Value, string Label);

/// <summary>
/// One row of the Levers page: a choice per mode from <see cref="Options"/>, or a number typed in when
/// <see cref="IsNumeric"/> (the GPU limit). A disabled row says why in <see cref="Note"/>.
/// </summary>
public sealed record LeverSettingRow(
    LeverSetting Setting,
    string Title,
    string? Note,
    bool Enabled,
    bool IsNumeric,
    IReadOnlyList<SettingOption> Options,
    int? Station,
    int? Reserve)
{
    public int? For(PowerModeKind mode) => mode switch
    {
        PowerModeKind.Station => Station,
        PowerModeKind.Reserve => Reserve,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}

/// <summary>
/// The decision logic of the Levers page: rows from the config and this PC, validation and clamping of a change,
/// and which levers a change re-applies at once (only for the current saving mode).
/// </summary>
public static class LeverSettings
{
    public const int MinCpuPercent = 20;
    public const int CpuStep = 5;
    public const int BrightnessStep = 5;
    public const int MinGpuW = 1;
    public const int MaxGpuW = 2000;
    private const int SecondsPerMinute = 60;

    public const string CpuNote = "Windows caps the CPU by % of max state, not by watts; the estimate shows the result.";

    private static readonly int[] MinuteChoices = [0, 1, 2, 3, 5, 10, 15, 20, 30, 45, 60, 120, 180, 240, 300];

    public static IReadOnlyList<LeverSettingRow> Build(AppConfig config, MachineInfo machine)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(machine);
        var rows = new List<LeverSettingRow>
        {
            Row(config, LeverSetting.CpuMaxPercent, "CPU max", null, true, false, [.. Steps(MinCpuPercent, 100, CpuStep).Select(Percent)]),
            Row(config, LeverSetting.Boost, "CPU boost", null, true, false, [new(0, "Off"), new(1, "On")]),
            Row(config, LeverSetting.GpuLimitW, "GPU limit", machine.Gpu is { } gpu ? $"{gpu.RangeText}, watts" : MachineInfo.NoNvidiaGpu + " detected", machine.Gpu is not null, true, []),
            Row(config, LeverSetting.BrightnessPercent, "Brightness", null, true, false, [Keep, .. Steps(0, 100, BrightnessStep).Select(Percent)]),
            Row(config, LeverSetting.RefreshHz, "Refresh rate", null, true, false, [Keep, .. machine.RefreshRates.Distinct().Order().Select(Hz)]),
            Row(config, LeverSetting.SecondaryDisplays, "Other displays", machine.Monitors.Count > 1 ? null : "One display connected", true, false, [new(0, "On"), new(1, "Off")]),
            Row(config, LeverSetting.DisplayOffMinutes, "Display off after", null, true, false, [.. MinuteChoices.Select(Minutes)]),
            Row(config, LeverSetting.HibernateMinutes, "Hibernate after", null, true, false, [.. MinuteChoices.Select(Minutes)]),
            Row(config, LeverSetting.Docker, "Docker Desktop", machine.DockerInstalled ? null : MachineInfo.DockerNotInstalled, machine.DockerInstalled, false, [new(0, "Keep running"), new(1, "Stop")]),
        };
        if (machine.WallpaperEngineInstalled)
        {
            rows.Add(Row(config, LeverSetting.WallpaperEngine, "Wallpaper Engine", null, true, false, [new(0, "Keep"), new(1, "Pause")]));
        }

        return rows;
    }

    /// <summary>The value of one setting in a mode's config.</summary>
    public static int? ValueOf(ModeConfig mode, LeverSetting setting)
    {
        ArgumentNullException.ThrowIfNull(mode);
        return setting switch
        {
            LeverSetting.CpuMaxPercent => mode.Scheme.MaxProcessorPercent,
            LeverSetting.Boost => mode.Scheme.Boost ? 1 : 0,
            LeverSetting.GpuLimitW => mode.GpuLimitWatts,
            LeverSetting.BrightnessPercent => mode.BrightnessPercent,
            LeverSetting.RefreshHz => mode.RefreshRateHz,
            LeverSetting.SecondaryDisplays => mode.SecondaryDisplaysOff ? 1 : 0,
            LeverSetting.DisplayOffMinutes => mode.Scheme.DisplayOffSeconds / SecondsPerMinute,
            LeverSetting.HibernateMinutes => mode.Scheme.HibernateSeconds / SecondsPerMinute,
            LeverSetting.Docker => mode.StopDocker ? 1 : 0,
            LeverSetting.WallpaperEngine => mode.PauseWallpaperEngine ? 1 : 0,
            _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, null),
        };
    }

    /// <summary>
    /// Store a value, clamped to what the lever accepts (CPU 20–100 % in steps of 5, the GPU's own range, brightness
    /// 0–100 %, minutes ≥ 0). Returns the value stored, which the page shows.
    /// </summary>
    public static int? Apply(ModeConfig mode, LeverSetting setting, int? value, GpuInfo? gpu)
    {
        ArgumentNullException.ThrowIfNull(mode);
        switch (setting)
        {
            case LeverSetting.CpuMaxPercent:
                mode.Scheme.MaxProcessorPercent = Math.Clamp((int)Math.Round((value ?? 100) / (double)CpuStep) * CpuStep, MinCpuPercent, 100);
                mode.Scheme.MinProcessorPercent = Math.Min(mode.Scheme.MinProcessorPercent, mode.Scheme.MaxProcessorPercent);
                break;
            case LeverSetting.Boost:
                mode.Scheme.Boost = value == 1;
                break;
            case LeverSetting.GpuLimitW:
                mode.GpuLimitWatts = value is { } watts ? Math.Clamp(watts, gpu?.MinLimitW ?? MinGpuW, gpu?.MaxLimitW ?? MaxGpuW) : null;
                break;
            case LeverSetting.BrightnessPercent:
                mode.BrightnessPercent = value is { } percent ? Math.Clamp(percent, 0, 100) : null;
                break;
            case LeverSetting.RefreshHz:
                mode.RefreshRateHz = value is { } hz ? Math.Clamp(hz, 1, 1000) : null;
                break;
            case LeverSetting.SecondaryDisplays:
                mode.SecondaryDisplaysOff = value == 1;
                break;
            case LeverSetting.DisplayOffMinutes:
                mode.Scheme.DisplayOffSeconds = Seconds(value);
                break;
            case LeverSetting.HibernateMinutes:
                mode.Scheme.HibernateSeconds = Seconds(value);
                break;
            case LeverSetting.Docker:
                mode.StopDocker = value == 1;
                break;
            case LeverSetting.WallpaperEngine:
                mode.PauseWallpaperEngine = value == 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(setting), setting, null);
        }

        return ValueOf(mode, setting);
    }

    /// <summary>A typed GPU limit: whole watts, or empty for "leave the limit alone".</summary>
    public static bool TryParseWatts(string? text, out int? watts)
    {
        var trimmed = (text ?? string.Empty).Trim().TrimEnd('W', 'w').Trim();
        if (trimmed.Length == 0)
        {
            watts = null;
            return true;
        }

        var ok = int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0;
        watts = ok ? parsed : null;
        return ok;
    }

    /// <summary>The levers a change re-applies at once: only for the current saving mode.</summary>
    public static IReadOnlyList<LeverKind> ImmediateLevers(PowerModeKind current, PowerModeKind changed, LeverSetting setting)
    {
        if (current != changed || current == PowerModeKind.Grid)
        {
            return [];
        }

        return setting switch
        {
            LeverSetting.CpuMaxPercent or LeverSetting.Boost or LeverSetting.DisplayOffMinutes or LeverSetting.HibernateMinutes => [LeverKind.PowerScheme],
            LeverSetting.GpuLimitW => [LeverKind.Gpu],
            LeverSetting.BrightnessPercent => [LeverKind.Brightness],
            LeverSetting.RefreshHz => [LeverKind.DisplayMode, LeverKind.Brightness],
            LeverSetting.SecondaryDisplays => [LeverKind.SecondaryDisplays, LeverKind.DisplayMode, LeverKind.Brightness],
            LeverSetting.Docker => [LeverKind.Docker],
            LeverSetting.WallpaperEngine => [LeverKind.WallpaperEngine],
            _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, null),
        };
    }

    private static LeverSettingRow Row(AppConfig config, LeverSetting setting, string title, string? note, bool enabled, bool numeric, List<SettingOption> options)
    {
        var station = ValueOf(config.Modes.Station, setting);
        var reserve = ValueOf(config.Modes.Reserve, setting);
        if (!numeric)
        {
            // A value set in config.json that is not one of the steps is still shown as it is.
            foreach (var value in new[] { station, reserve }.Where(v => !options.Exists(o => o.Value == v)).Distinct())
            {
                options.Add(Label(setting, value));
            }

            options.Sort((a, b) => (a.Value ?? int.MinValue).CompareTo(b.Value ?? int.MinValue));
        }

        return new LeverSettingRow(setting, title, note, enabled, numeric, options, station, reserve);
    }

    private static SettingOption Label(LeverSetting setting, int? value) => setting switch
    {
        _ when value is null => Keep,
        LeverSetting.CpuMaxPercent or LeverSetting.BrightnessPercent => Percent(value.Value),
        LeverSetting.RefreshHz => Hz(value.Value),
        LeverSetting.DisplayOffMinutes or LeverSetting.HibernateMinutes => Minutes(value.Value),
        _ => new SettingOption(value, value.Value.ToString(CultureInfo.InvariantCulture)),
    };

    private static SettingOption Keep { get; } = new(null, "Keep");

    private static SettingOption Percent(int value) => new(value, string.Create(CultureInfo.InvariantCulture, $"{value} %"));

    private static SettingOption Hz(int value) => new(value, string.Create(CultureInfo.InvariantCulture, $"{value} Hz"));

    private static SettingOption Minutes(int minutes) => new(minutes, minutes == 0 ? "Never" : FlyoutStateBuilder.Duration(minutes * SecondsPerMinute));

    private static IEnumerable<int> Steps(int from, int to, int step)
    {
        for (var value = from; value <= to; value += step)
        {
            yield return value;
        }
    }

    private static int Seconds(int? minutes) => (int)Math.Min(Math.Max(minutes ?? 0, 0) * (long)SecondsPerMinute, int.MaxValue);
}
