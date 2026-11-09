using System.Globalization;
using PowerMode.Core.Config;
using PowerMode.Core.Input;
using PowerMode.Core.Levers;
using PowerMode.Core.Model;
using PowerMode.Core.Power;

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
    HotkeyConfig Hotkeys,
    PowerCard? Power = null,
    MachineInfo? Machine = null);

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

        return new FlyoutState(mode, applying, status, tone, banner, rows, Footer(input.Hotkeys), input.StartWithWindows, input.Power);
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

    /// <summary>
    /// Tray tooltip: <c>PowerMode · Station · ≈ 142 W</c> (the estimated total); while a switch runs
    /// <c>PowerMode · Applying Station… · ≈ 142 W</c>.
    /// </summary>
    public static string Tooltip(PowerModeKind mode, bool applying, double? totalWatts)
    {
        var text = $"PowerMode{Dot}{(applying ? $"Applying {ModeNames.Display(mode)}{Ellipsis}" : ModeNames.Display(mode))}";
        return totalWatts is { } watts ? text + string.Create(CultureInfo.InvariantCulture, $"{Dot}{PowerCards.Approx} {watts:0} W") : text;
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
                // The row's fact holds now: set, already set, or not used in this mode (all levers report only those).
                glyph = results.Count > 0 ? RowGlyph.Ok : RowGlyph.None;
            }
        }

        // Only a choice when the config would stop Docker (and Docker exists); otherwise the row says why not.
        var machine = input.Machine ?? MachineInfo.Assumed;
        var showToggle = row == LeverRow.Docker && input.Target.Mode == PowerModeKind.Station && input.Target.DockerStopConfigured && machine.DockerInstalled;
        var monitors = input.Live.AllMonitors();
        return new RowState(row, LeverRows.Title(row), Describe(row, input.Target, monitors.Select(m => m.Name).ToList(), machine), Live(row, input.Live, input.Target), glyph, reason, showToggle, input.KeepDocker);
    }

    /// <summary>
    /// What the mode set for a row, one fact per line: one line per monitor when there are several
    /// (<c>M27Q2 · 35 % · 60 Hz</c>), and what is missing on this PC (<c>No NVIDIA GPU</c>).
    /// </summary>
    public static IReadOnlyList<string> Describe(LeverRow row, TargetState target, IReadOnlyList<string>? monitorNames = null, MachineInfo? machine = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        machine ??= MachineInfo.Assumed;
        switch (row)
        {
            case LeverRow.Gpu when machine.Gpu is null:
                return [MachineInfo.NoNvidiaGpu];
            case LeverRow.Docker when !machine.DockerInstalled:
                return [MachineInfo.DockerNotInstalled];
        }

        return target.Mode == PowerModeKind.Grid
            ? DescribeGrid(row, target, machine)
            : DescribeSaving(row, target, monitorNames is { Count: > 1 } names ? names : [], machine);
    }

    private static string[] DescribeGrid(LeverRow row, TargetState target, MachineInfo machine) => row switch
    {
        LeverRow.Cpu => ["Your power scheme"],
        LeverRow.Gpu => ["Your power limit"],
        LeverRow.Monitor => ["Your settings"],
        LeverRow.Display => ["Your timeouts"],
        LeverRow.Docker => [target.Docker is { Running: true } d && d.ContainersToStart.Count > 0 ? $"Running{Dot}{Count(d.ContainersToStart.Count, "container")}" : "As you left it"],
        LeverRow.Apps => AppsGrid(target, machine),
        LeverRow.Efficiency => ["Off"],
        LeverRow.Services => target.Services.Start.Count > 0 ? ["Running: " + FriendlyNames.ServiceList(target.Services.Start)] : ["As you left them"],
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, null),
    };

    private static string[] AppsGrid(TargetState target, MachineInfo machine)
    {
        var lines = new List<string> { "Nothing closed" };
        if (target.Wallpaper == WallpaperAction.Play && machine.WallpaperEngineInstalled)
        {
            lines.Add("Wallpaper Engine playing");
        }

        return [.. lines];
    }

    private static string[] DescribeSaving(LeverRow row, TargetState target, IReadOnlyList<string> monitorNames, MachineInfo machine)
    {
        switch (row)
        {
            case LeverRow.Cpu:
                return [target.Scheme?.Configure is { } cpu ? CpuFact(cpu) : Unchanged];
            case LeverRow.Gpu:
                return [target.Gpu?.LimitWatts is { } watts ? $"Limit {watts} W" : Unchanged];
            case LeverRow.Monitor:
                return MonitorLines(target, monitorNames);
            case LeverRow.Display:
                return [target.Scheme?.Configure is { } scheme ? TimeoutFact(scheme) : Unchanged];
            case LeverRow.Docker:
                return [DockerFact(target)];
            case LeverRow.Apps:
                var apps = new List<string>();
                if (target.Apps.Close.Count > 0)
                {
                    apps.Add("Closed: " + FriendlyNames.ProcessList(target.Apps.Close));
                }

                if (target.Wallpaper == WallpaperAction.Pause && machine.WallpaperEngineInstalled)
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

    /// <summary>One monitor: <c>35 % · 60 Hz</c>. Several: <c>M27Q2 · 35 % · 60 Hz</c> per monitor, <c>T24i-30 · Off</c> when secondary displays are off.</summary>
    private static string[] MonitorLines(TargetState target, IReadOnlyList<string> names)
    {
        var fact = MonitorFact(target, string.Empty, Dot) ?? Unchanged;
        if (names.Count == 0)
        {
            return [fact];
        }

        var off = target.Topology is { PrimaryOnly: true };
        return [.. names.Select((name, i) => $"{name}{Dot}{(off && i > 0 ? "Off" : fact)}")];
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

    private static string DockerFact(TargetState target) => target.Docker switch
    {
        null => Unchanged,
        { Running: false, SnapshotContainerCount: > 0 } d => $"Stopped{Dot}was {Count(d.SnapshotContainerCount, "container")}",
        { Running: false } => "Stopped",
        { Running: true } => target.DockerStopConfigured ? "Kept running" : "Kept running (config)",
    };

    private static string? Live(LeverRow row, LiveValues live, TargetState target) => row switch
    {
        LeverRow.Cpu when live.CpuGhz is { } ghz => live.CpuUtilityPercent is { } util
            ? string.Create(CultureInfo.InvariantCulture, $"{ghz:0.0} GHz{Dot}{util:0} %")
            : string.Create(CultureInfo.InvariantCulture, $"{ghz:0.0} GHz"),
        LeverRow.Gpu when live.GpuWatts is { } watts => string.Create(CultureInfo.InvariantCulture, $"{watts:0} W"),
        LeverRow.Monitor => MonitorLive(live, target),
        LeverRow.Docker when live.DockerEngineRunning is { } running => running ? $"{live.DockerContainersRunning ?? 0} running" : "engine stopped",
        _ => null,
    };

    /// <summary>One line per monitor, in the order of the set lines: <c>35 % · 60 Hz</c>, or <c>Off</c>.</summary>
    private static string? MonitorLive(LiveValues live, TargetState target)
    {
        var lines = live.AllMonitors().Select(m => m.Active
            ? string.Join(Dot, new[] { m.BrightnessPercent is { } b ? $"{b} %" : null, m.RefreshHz is { } r ? $"{RefreshRates.Display(r, RequestedHz(target))} Hz" : null }.OfType<string>())
            : "Off").ToList();
        return lines.All(l => l.Length == 0) ? null : string.Join(Environment.NewLine, lines);
    }

    /// <summary>The rate the mode sets, or the one Grid restores on the first display (the one the live probe reads).</summary>
    private static int? RequestedHz(TargetState target) =>
        target.Refresh is { } refresh ? refresh.RefreshHz ?? (refresh.Restore.Count > 0 ? refresh.Restore[0].RefreshHz : null) : null;

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
