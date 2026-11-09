using System.Text.Json;
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
    private readonly Lock _gate = new();
    private string? _lastWritten;

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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Bad content must never take the app down: keep the last good config.
            _log.Error(Category, $"Cannot load {_path}, keeping last good config", ex);
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

    /// <summary>Runs on the debounce timer's thread, where an escaping exception would end the process.</summary>
    internal void Reload()
    {
        try
        {
            lock (_gate)
            {
                if (IsOwnWrite() || !Load())
                {
                    return;
                }
            }

            _log.Write(Category, "Reloaded config");
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error(Category, "Config reload failed", ex);
        }
    }

    /// <summary>The file still holds exactly what <see cref="Update"/> wrote: the watcher saw our own write, nothing to reload.</summary>
    private bool IsOwnWrite()
    {
        try
        {
            return _lastWritten is not null && File.Exists(_path) && string.Equals(File.ReadAllText(_path), _lastWritten, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            // Being written by someone else right now; Load decides.
            return false;
        }
    }

    /// <summary>Change one saving mode (the settings pages); see <see cref="Update"/>.</summary>
    public bool UpdateMode(PowerModeKind mode, Action<ModeConfig> change, string what)
    {
        ArgumentNullException.ThrowIfNull(change);
        _ = AppConfig.CreateDefault().Modes.For(mode) ?? throw new ArgumentOutOfRangeException(nameof(mode), mode, "Only Station and Reserve have settings.");
        return Update(config => change(config.Modes.For(mode)!), $"{ModeNames.Display(mode)}: {what}");
    }

    /// <summary>
    /// Change the config. config.json is rewritten atomically with only the changed keys written (every other key
    /// stays as it is), and <see cref="Current"/> is the result at once. The watcher's event for this write does not
    /// reload or raise <see cref="Changed"/>. False, and nothing written, when the file on disk is not a valid config
    /// right now (the user is editing it), when the change makes it invalid, or when it cannot be written.
    /// </summary>
    public bool Update(Action<AppConfig> change, string what)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var before = Current;
            var after = ConfigLoader.Parse(ConfigLoader.Serialize(Current)).Config!;
            change(after);
            try
            {
                var text = File.Exists(_path) ? File.ReadAllText(_path) : ConfigLoader.Serialize(Current);
                var edited = ConfigEditor.SetChanges(text, before, after);
                var parsed = ConfigLoader.Parse(edited);
                if (!parsed.IsValid)
                {
                    _log.Write(Category, $"Not saving {what}: config.json would be invalid: " + string.Join("; ", parsed.Errors));
                    return false;
                }

                WriteAtomically(edited);
                _lastWritten = edited;
                Current = parsed.Config!;
                _log.Write(Category, $"Saved {what}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _log.Error(Category, $"Cannot save {what} to {_path}", ex);
                return false;
            }
        }
    }

    /// <summary>Write next to the file, then replace it in one rename: a reader never sees half a file.</summary>
    private void WriteAtomically(string text)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, _path, overwrite: true);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }
}
