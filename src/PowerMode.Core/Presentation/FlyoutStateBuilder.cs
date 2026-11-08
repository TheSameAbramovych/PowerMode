using System.Globalization;
using PowerMode.Core.Config;
using PowerMode.Core.Input;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;

namespace PowerMode.Core.Presentation;

public sealed record FlyoutInput(
    TargetState Target,
    IReadOnlyDictionary<LeverKind, LeverResult> Results,
    IReadOnlySet<LeverKind> Applying,
    LiveValues Live,
    DateTimeOffset? Since,
    DateTimeOffset Now,
    bool IsElevated,
    bool KeepDocker,
    bool StartWithWindows,
    HotkeyConfig Hotkeys);

/// <summary>Turns a target state, lever results and live values into the text the flyout, CLI and toast show.</summary>
public static class FlyoutStateBuilder
{
    public const string Dot = " · ";
    private const string Ellipsis = "…";
    private const string SwitchModesSuffix = " switch modes";
    private const string Unchanged = "Unchanged";

    public static FlyoutState Build(FlyoutInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var mode = input.Target.Mode;
        var applying = input.Applying.Count > 0;
        var failed = input.Results.Where(r => r.Value.IsFailure).ToList();
        var rows = LeverRows.Ordered.Select(row => BuildRow(row, input)).ToList();

        string status;
        var tone = StatusTone.Normal;
        if (applying)
        {
            status = $"Applying {ModeNames.Display(mode)}{Ellipsis}";
        }
        else if (failed.Count > 0)
        {
            status = $"{ModeNames.Display(mode)}{Dot}{failed.Count} {(failed.Count == 1 ? "lever" : "levers")} failed";
            tone = StatusTone.Warning;
        }
        else
        {
            status = input.Since is { } since ? $"{ModeNames.Display(mode)}{Dot}since {FormatSince(since, input.Now)}" : ModeNames.Display(mode);
        }

        string? banner = null;
        if (!input.IsElevated && !applying)
        {
            var nouns = failed.Where(r => r.Value.NeedsElevation).Select(r => LeverRows.ElevationNoun(r.Key)).ToList();
            if (nouns.Count > 0)
            {
                banner = $"{JoinWithAnd(nouns)} {(nouns.Count == 1 ? "needs" : "need")} administrator rights.";
                banner = char.ToUpperInvariant(banner[0]) + banner[1..];
            }
        }

        return new FlyoutState(mode, applying, status, tone, banner, rows, Footer(input.Hotkeys), input.StartWithWindows);
    }

    /// <summary>Balloon title and text shown after a switch.</summary>
    public static (string Title, string Text) Toast(TargetState target, IReadOnlyDictionary<LeverKind, LeverResult> results)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(results);
        var title = $"{ModeNames.Display(target.Mode)} mode";
        var parts = new List<string>();
        if (target.Mode == PowerModeKind.Grid)
        {
            parts.Add("Your settings restored");
        }
        else
        {
            if (target.Scheme?.Configure is { } scheme)
            {
                parts.Add(CpuFact(scheme));
            }

            if (MonitorFact(target, "monitor ", " @ ") is { } monitor)
            {
                parts.Add(monitor);
            }

            if (target.Gpu?.LimitWatts is { } watts)
            {
                parts.Add($"GPU {watts} W");
            }

            if (target.Docker is { } docker)
            {
                parts.Add(docker.Running ? "Docker kept" : "Docker stopped");
            }
        }

        var failedCount = results.Values.Count(r => r.IsFailure);
        if (failedCount > 0)
        {
            parts.Add($"{failedCount} {(failedCount == 1 ? "lever" : "levers")} failed");
        }

        return (title, string.Join(Dot, parts));
    }

    public static string Footer(HotkeyConfig hotkeys)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        var keys = ModeNames.All.Select(hotkeys.For).Where(k => Hotkey.TryParse(k, out _)).ToList();
        if (keys.Count == 0)
        {
            return "No hotkeys";
        }

        var prefixes = keys.Select(k => k[..(k.LastIndexOf('+') + 1)]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var text = prefixes.Count == 1 && keys.Count > 1
            ? keys[0] + string.Concat(keys.Skip(1).Select(k => Dot + k[(k.LastIndexOf('+') + 1)..]))
            : string.Join(Dot, keys);
        return text + SwitchModesSuffix;
    }

    private static RowState BuildRow(LeverRow row, FlyoutInput input)
    {
        var kinds = LeverRows.LeversOf(row);
        RowGlyph glyph;
        string? reason = null;
        if (kinds.Any(input.Applying.Contains))
        {
            glyph = RowGlyph.Applying;
        }
        else
        {
            var results = kinds.Where(input.Results.ContainsKey).Select(k => input.Results[k]).ToList();
            var failures = results.Where(r => r.IsFailure).ToList();
            if (failures.Count > 0)
            {
                glyph = RowGlyph.Failed;
                reason = string.Join("; ", failures.Select(f => f.Detail).Distinct(StringComparer.Ordinal));
            }
            else
            {
                glyph = results.Any(r => r.Status == LeverStatus.Ok) ? RowGlyph.Ok : RowGlyph.None;
            }
        }

        var showToggle = row == LeverRow.Docker && input.Target.Mode == PowerModeKind.Station;
        return new RowState(row, LeverRows.Title(row), Describe(row, input.Target), Live(row, input.Live), glyph, reason, showToggle, input.KeepDocker);
    }

    /// <summary>What the mode set for a row, one fact per line.</summary>
    public static IReadOnlyList<string> Describe(LeverRow row, TargetState target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Mode == PowerModeKind.Grid ? DescribeGrid(row, target) : DescribeSaving(row, target);
    }

    private static string[] DescribeGrid(LeverRow row, TargetState target) => row switch
    {
        LeverRow.Cpu => ["Your power scheme"],
        LeverRow.Gpu => ["Your power limit"],
        LeverRow.Monitor => ["Your brightness" + Dot + "refresh rate"],
        LeverRow.Display => ["Your timeouts"],
        LeverRow.Docker => [target.Docker is { Running: true } d && d.ContainersToStart.Count > 0 ? $"Running{Dot}{Count(d.ContainersToStart.Count, "container")}" : "As you left it"],
        LeverRow.Apps => AppsGrid(target),
        LeverRow.Efficiency => ["Off"],
        LeverRow.Services => target.Services.Start.Count > 0 ? ["Running: " + FriendlyNames.ServiceList(target.Services.Start)] : ["As you left them"],
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, null),
    };

    private static string[] AppsGrid(TargetState target)
    {
        var lines = new List<string>
        {
            target.Apps.Relaunch.Count > 0 ? "Relaunched: " + FriendlyNames.ProcessList(target.Apps.Relaunch.Select(p => p.Name)) : "Nothing closed",
        };
        if (target.Wallpaper == WallpaperAction.Play)
        {
            lines.Add("Wallpaper Engine playing");
        }

        return [.. lines];
    }

    private static string[] DescribeSaving(LeverRow row, TargetState target)
    {
        switch (row)
        {
            case LeverRow.Cpu:
                return [target.Scheme?.Configure is { } cpu ? CpuFact(cpu) : Unchanged];
            case LeverRow.Gpu:
                return [target.Gpu?.LimitWatts is { } watts ? $"Limit {watts} W" : Unchanged];
            case LeverRow.Monitor:
                return [MonitorFact(target, string.Empty, Dot) ?? Unchanged];
            case LeverRow.Display:
                return [target.Scheme?.Configure is { } scheme ? TimeoutFact(scheme) : Unchanged];
            case LeverRow.Docker:
                return [DockerFact(target.Docker)];
            case LeverRow.Apps:
                var apps = new List<string>();
                if (target.Apps.Close.Count > 0)
                {
                    apps.Add("Closed: " + FriendlyNames.ProcessList(target.Apps.Close));
                }

                if (target.Wallpaper == WallpaperAction.Pause)
                {
                    apps.Add("Wallpaper Engine paused");
                }

                return apps.Count > 0 ? [.. apps] : [Unchanged];
            case LeverRow.Efficiency:
                return [target.Efficiency.ProcessNames.Count > 0 ? FriendlyNames.ProcessList(target.Efficiency.ProcessNames) : "Off"];
            case LeverRow.Services:
                return [target.Services.Stop.Count > 0 ? "Stopped: " + FriendlyNames.ServiceList(target.Services.Stop) : Unchanged];
            default:
                throw new ArgumentOutOfRangeException(nameof(row), row, null);
        }
    }

    private static string CpuFact(SchemeConfig scheme) =>
        $"Boost {(scheme.Boost ? "on" : "off")}{Dot}max {scheme.MaxProcessorPercent} %";

    private static string TimeoutFact(SchemeConfig scheme)
    {
        var display = scheme.DisplayOffSeconds > 0 ? $"Off after {Duration(scheme.DisplayOffSeconds)}" : "Stays on";
        var parts = new List<string> { display };
        if (scheme.SleepSeconds > 0)
        {
            parts.Add($"Sleep {Duration(scheme.SleepSeconds)}");
        }

        parts.Add(scheme.HibernateSeconds > 0 ? $"Hibernate {Duration(scheme.HibernateSeconds)}" : "No hibernate");
        return string.Join(Dot, parts);
    }

    private static string? MonitorFact(TargetState target, string prefix, string separator)
    {
        var brightness = target.Monitor?.BrightnessPercent;
        var hz = target.Refresh?.RefreshHz;
        return (brightness, hz) switch
        {
            ({ } b, { } r) => $"{prefix}{b} %{separator}{r} Hz",
            ({ } b, null) => $"{prefix}{b} %",
            (null, { } r) => $"{prefix}{r} Hz",
            _ => null,
        };
    }

    private static string DockerFact(DockerTarget? docker) => docker switch
    {
        null => Unchanged,
        { Running: false, SnapshotContainerCount: > 0 } d => $"Stopped{Dot}was {Count(d.SnapshotContainerCount, "container")}",
        { Running: false } => "Stopped",
        { Running: true } => "Kept running",
    };

    private static string? Live(LeverRow row, LiveValues live) => row switch
    {
        LeverRow.Cpu when live.CpuGhz is { } ghz => live.CpuUtilityPercent is { } util
            ? string.Create(CultureInfo.InvariantCulture, $"{ghz:0.0} GHz{Dot}{util:0} %")
            : string.Create(CultureInfo.InvariantCulture, $"{ghz:0.0} GHz"),
        LeverRow.Gpu when live.GpuWatts is { } watts => string.Create(CultureInfo.InvariantCulture, $"{watts:0} W"),
        LeverRow.Monitor when live.BrightnessPercent is not null || live.RefreshHz is not null =>
            string.Join(Dot, new[] { live.BrightnessPercent is { } b ? $"{b} %" : null, live.RefreshHz is { } r ? $"{r} Hz" : null }.OfType<string>()),
        LeverRow.Docker when live.DockerEngineRunning is { } running => running ? $"{live.DockerContainersRunning ?? 0} running" : "engine stopped",
        _ => null,
    };

    public static string Duration(int seconds)
    {
        if (seconds % 3600 == 0)
        {
            return $"{seconds / 3600} h";
        }

        if (seconds >= 3600 && seconds % 60 == 0)
        {
            return $"{seconds / 3600} h {seconds % 3600 / 60} min";
        }

        return seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} s";
    }

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? string.Empty : "s")}";

    private static string FormatSince(DateTimeOffset since, DateTimeOffset now) =>
        since.Date == now.Date
            ? since.ToString("HH:mm", CultureInfo.InvariantCulture)
            : since.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);

    internal static string JoinWithAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
