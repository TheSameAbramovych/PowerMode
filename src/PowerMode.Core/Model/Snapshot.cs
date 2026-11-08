using PowerMode.Core.Levers;

namespace PowerMode.Core.Model;

/// <summary>Brightness/contrast of one physical monitor, keyed by <see cref="Id"/> (display device + index).</summary>
public sealed record MonitorLevel(string Id, int Brightness, int? Contrast = null);

/// <summary>Display mode of one display device (e.g. <c>\\.\DISPLAY1</c>).</summary>
public sealed record DisplayModeInfo(string Device, int Width, int Height, int RefreshHz, int BitsPerPixel);

public sealed record GpuSnapshot(uint LimitMilliwatts, uint DefaultMilliwatts);

public sealed record DockerSnapshot(bool EngineRunning, IReadOnlyList<string> RunningContainers);

/// <summary>A process that one of the close lists may close; enough to relaunch it.</summary>
public sealed record CapturedProcess(string Name, string Path, string Arguments);

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

    public GpuSnapshot? Gpu { get; set; }

    /// <summary>Wallpaper Engine was running (and therefore presumably playing) when the baseline was taken.</summary>
    public bool WallpaperEngineRunning { get; set; }

    public DockerSnapshot? Docker { get; set; }

    public List<CapturedProcess> Processes { get; set; } = [];

    /// <summary>Service name → was running.</summary>
    public Dictionary<string, bool> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Copy one lever's part from an older snapshot. Used when a previous Grid restore of that lever failed,
    /// so the fresh capture would otherwise record the still-reduced value as the new baseline.
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
                break;
            case LeverKind.Services:
                Services = new Dictionary<string, bool>(older.Services, StringComparer.OrdinalIgnoreCase);
                break;
            case LeverKind.Efficiency:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }
}
