using PowerMode.Core.Logging;

namespace PowerMode.Core.Config;

/// <summary>
/// Owns config.json: creates it with the defaults on first run, reloads it when it changes on disk, and keeps
/// the last good config when the file becomes invalid.
/// </summary>
public sealed class ConfigStore : IDisposable
{
    private const string Category = "config";
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    private readonly string _path;
    private readonly ILog _log;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;

    public ConfigStore(string path, ILog log)
    {
        _path = path;
        _log = log;
    }

    public AppConfig Current { get; private set; } = AppConfig.CreateDefault();

    public string Path => _path;

    /// <summary>Raised (on a thread-pool thread) after a valid config was reloaded from disk.</summary>
    public event EventHandler? Changed;

    /// <summary>Load the file, creating it with defaults when missing. Returns false when it was invalid.</summary>
    public bool Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, ConfigLoader.Serialize(Current));
                _log.Write(Category, $"Created {_path} with defaults");
                return true;
            }

            return Apply(File.ReadAllText(_path));
        }
        catch (IOException ex)
        {
            _log.Error(Category, $"Cannot read {_path}, keeping last good config", ex);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Error(Category, $"Cannot read {_path}, keeping last good config", ex);
            return false;
        }
    }

    /// <summary>Apply config text; invalid text keeps the last good config and logs why.</summary>
    public bool Apply(string json)
    {
        var result = ConfigLoader.Parse(json);
        if (!result.IsValid)
        {
            _log.Write(Category, "Invalid config, keeping last good: " + string.Join("; ", result.Errors));
            return false;
        }

        Current = result.Config!;
        return true;
    }

    public void StartWatching()
    {
        var directory = System.IO.Path.GetDirectoryName(_path)!;
        _debounceTimer = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(directory, System.IO.Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => _debounceTimer?.Change(Debounce, Timeout.InfiniteTimeSpan);

    private void Reload()
    {
        if (Load())
        {
            _log.Write(Category, "Reloaded config");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }
}
