using System.Text.Json;
using PowerMode.Core.Logging;

namespace PowerMode.Core.State;

/// <summary>Reads and atomically writes state.json. A missing or unreadable file means "Grid, no snapshot".</summary>
public sealed class StateStore
{
    private const string Category = "state";
    private const string TempSuffix = ".tmp";

    private readonly string _path;
    private readonly ILog _log;
    private readonly Lock _gate = new();

    public StateStore(string path, ILog log)
    {
        _path = path;
        _log = log;
    }

    public string Path => _path;

    public PersistedState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new PersistedState();
            }

            return Deserialize(File.ReadAllText(_path)) ?? new PersistedState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Error(Category, $"Cannot read {_path}, starting from Grid", ex);
            return new PersistedState();
        }
    }

    public void Save(PersistedState state)
    {
        var json = Serialize(state);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                var temp = _path + TempSuffix;
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error(Category, $"Cannot write {_path}", ex);
            }
        }
    }

    public static string Serialize(PersistedState state) => JsonSerializer.Serialize(state, JsonDefaults.Options);

    public static PersistedState? Deserialize(string json) => JsonSerializer.Deserialize<PersistedState>(json, JsonDefaults.Options);
}
