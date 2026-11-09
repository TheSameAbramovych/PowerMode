using System.Text.Json;
using PowerMode.Core.Levers;
using PowerMode.Core.State;

namespace PowerMode.Core.Model;

/// <summary>Brightness/contrast of one physical monitor, keyed by <see cref="Id"/> (display device + index).</summary>
public sealed record MonitorLevel(string Id, int Brightness, int? Contrast = null);

/// <summary>Display mode of one display device (e.g. <c>\\.\DISPLAY1</c>).</summary>
public sealed record DisplayModeInfo(string Device, int Width, int Height, int RefreshHz, int BitsPerPixel)
{
    /// <summary>Same device, resolution and colour depth, refresh rate within <see cref="RefreshRates.Tolerance"/>.</summary>
    public bool Matches(DisplayModeInfo other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Device, other.Device, StringComparison.OrdinalIgnoreCase)
            && Width == other.Width && Height == other.Height && BitsPerPixel == other.BitsPerPixel
            && RefreshRates.Same(RefreshHz, other.RefreshHz);
    }
}

/// <summary>
/// Windows reports a fractional rate rounded down: a 60 Hz mode (59.94 Hz) reads back as 59 Hz after it was set.
/// Rates within one hertz are the same rate.
/// </summary>
public static class RefreshRates
{
    public const int Tolerance = 1;

    public static bool Same(int a, int b) => Math.Abs(a - b) <= Tolerance;

    /// <summary>The offered rate for <paramref name="wanted"/>: exactly it, else the closest within the tolerance, else null.</summary>
    public static int? Pick(IEnumerable<int> offered, int wanted)
    {
        ArgumentNullException.ThrowIfNull(offered);
        var candidates = offered.Where(hz => Same(hz, wanted)).ToList();
        return candidates.Count == 0 ? null : candidates.OrderBy(hz => Math.Abs(hz - wanted)).ThenByDescending(hz => hz).First();
    }

    /// <summary>What to show for a measured rate: the requested one when the measurement is within the tolerance of it.</summary>
    public static int Display(int measured, int? requested) =>
        requested is { } hz && Same(measured, hz) ? hz : measured;
}

/// <summary>
/// The active display configuration as Windows reports it (QueryDisplayConfig): <see cref="Data"/> is the paths and
/// modes, base64, for SetDisplayConfig; <see cref="ActiveDisplays"/> how many displays were on. <see cref="Adapters"/>
/// maps the adapter LUIDs inside <see cref="Data"/>, which change on every boot, to their device paths, so the layout
/// can be put back after a reboot; null in a topology saved by an older version.
/// </summary>
public sealed record DisplayTopology(int ActiveDisplays, string Data, IReadOnlyList<DisplayAdapter>? Adapters = null);

public sealed record GpuSnapshot(uint LimitMilliwatts, uint DefaultMilliwatts);

public sealed record DockerSnapshot(bool EngineRunning, IReadOnlyList<string> RunningContainers);

/// <summary>
/// A process that one of the close lists may close; enough to relaunch it. <see cref="AppUserModelId"/> is set for
/// packaged (MSIX) apps, which are relaunched through their AUMID; <see cref="Path"/> is then for display only.
/// </summary>
public sealed record CapturedProcess(string Name, string Path, string Arguments, string? AppUserModelId = null);

/// <summary>
/// The baseline captured once when leaving Grid. Each lever fills only its own part; Grid restores it lever
/// by lever. Persisted inside <c>state.json</c> as soon as it is captured.
/// </summary>
public sealed class Snapshot
{
    public DateTimeOffset CapturedAt { get; set; }

    public Guid? ActiveScheme { get; set; }

    public List<MonitorLevel> Monitors { get; set; } = [];

    public List<DisplayModeInfo> Displays { get; set; } = [];

    /// <summary>The active display topology (paths and modes, opaque to Core), so Grid can turn secondary displays back on.</summary>
    public DisplayTopology? Topology { get; set; }

    public GpuSnapshot? Gpu { get; set; }

    /// <summary>Wallpaper Engine was running (and therefore presumably playing) when the baseline was taken.</summary>
    public bool WallpaperEngineRunning { get; set; }

    public DockerSnapshot? Docker { get; set; }

    public List<CapturedProcess> Processes { get; set; } = [];

    /// <summary>Service name → was running.</summary>
    public Dictionary<string, bool> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Close-list names this baseline has looked at (running or not), so a name added to a list later is captured
    /// on its own. Null in snapshots written before it existed: <see cref="CoveredAppNames"/> falls back to the captured names.
    /// </summary>
    public List<string>? CoveredApps { get; set; }

    /// <summary>Service-list names this baseline has looked at; see <see cref="CoveredApps"/>.</summary>
    public List<string>? CoveredServices { get; set; }

    /// <summary>
    /// Parts whose read failed or was inconclusive. A saving mode does not reduce such a lever (the power scheme
    /// excepted: Grid then activates the fallback scheme), so Grid leaves it alone. Replaced, never changed in
    /// place, because the UI thread reads it while a switch runs.
    /// </summary>
    public HashSet<LeverKind> Missing { get; set; } = [];

    /// <summary>Parts a Grid restore already put back (or gave up on for good); Grid no longer owes them. Replaced, never changed in place.</summary>
    public HashSet<LeverKind> Restored { get; set; } = [];

    public IReadOnlyList<string> CoveredAppNames() => CoveredApps ?? [.. Processes.Select(p => p.Name)];

    public IReadOnlyList<string> CoveredServiceNames() => CoveredServices ?? [.. Services.Keys];

    /// <summary>Grid still has to restore this part.</summary>
    public bool Owes(LeverKind kind) => !Restored.Contains(kind) && !Missing.Contains(kind);

    public void SetMissing(LeverKind kind, bool missing) =>
        Missing = missing ? [.. Missing, kind] : [.. Missing.Where(k => k != kind)];

    /// <summary>A deep copy (a dry run completes a copy without touching the persisted baseline).</summary>
    public Snapshot Copy() => JsonSerializer.Deserialize<Snapshot>(JsonSerializer.Serialize(this, StateJson.Options), StateJson.Options)!;

    /// <summary>
    /// Copy one lever's part (and whether it is missing) from another snapshot: an older baseline that is still owed
    /// because its restore failed or the switch that took it was interrupted, or a capture retried on its own.
    /// </summary>
    public void CopyPartFrom(Snapshot older, LeverKind kind)
    {
        ArgumentNullException.ThrowIfNull(older);
        switch (kind)
        {
            case LeverKind.PowerScheme:
                ActiveScheme = older.ActiveScheme;
                break;
            case LeverKind.DisplayMode:
                Displays = [.. older.Displays];
                break;
            case LeverKind.Brightness:
                Monitors = [.. older.Monitors];
                break;
            case LeverKind.Gpu:
                Gpu = older.Gpu;
                break;
            case LeverKind.WallpaperEngine:
                WallpaperEngineRunning = older.WallpaperEngineRunning;
                break;
            case LeverKind.Docker:
                Docker = older.Docker;
                break;
            case LeverKind.Apps:
                Processes = [.. older.Processes];
                CoveredApps = older.CoveredApps is { } apps ? [.. apps] : null;
                break;
            case LeverKind.Services:
                Services = new Dictionary<string, bool>(older.Services, StringComparer.OrdinalIgnoreCase);
                CoveredServices = older.CoveredServices is { } services ? [.. services] : null;
                break;
            case LeverKind.SecondaryDisplays:
                Topology = older.Topology;
                break;
            case LeverKind.Efficiency:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        SetMissing(kind, older.Missing.Contains(kind));
    }
}
