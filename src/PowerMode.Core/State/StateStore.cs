using System.Globalization;
using System.Text;
using System.Text.Json;
using PowerMode.Core.Logging;

namespace PowerMode.Core.State;

/// <summary>
/// Reads and durably writes state.json. Writes go to a temp file flushed to disk and then replace state.json, keeping
/// the previous copy as state.json.bak, so a hard power-off never leaves a half-written state. A file that cannot be
/// parsed is kept as state.json.corrupt-* (never deleted) and the backup is used instead; only when both are bad does
/// the state start over from "Grid, no snapshot".
/// </summary>
public sealed class StateStore
{
    private const string Category = "state";
    private const string TempSuffix = ".tmp";
    private const string BackupSuffix = ".bak";
    private const string CorruptSuffix = ".corrupt-";
    private const string CorruptStampFormat = "yyyyMMddHHmmss";
    private const int BufferSize = 4096;

    private readonly string _path;
    private readonly ILog _log;
    private readonly Lock _gate = new();

    public StateStore(string path, ILog log)
    {
        _path = path;
        _log = log;
    }

    public string Path => _path;

    public string BackupPath => _path + BackupSuffix;

    public PersistedState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new PersistedState();
            }

            if (TryRead(_path, quarantine: true) is { } state)
            {
                return state;
            }

            if (File.Exists(BackupPath) && TryRead(BackupPath, quarantine: false) is { } backup)
            {
                _log.Write(Category, $"Recovered the state from {BackupPath}");
                return backup;
            }

            _log.Write(Category, $"No readable state in {_path} or its backup: starting from Grid, the baseline is lost");
            return new PersistedState();
        }
    }

    public void Save(PersistedState state)
    {
        lock (_gate)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(Serialize(state));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                var temp = _path + TempSuffix;
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(_path))
                {
                    File.Replace(temp, _path, BackupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, _path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                // InvalidOperation/Argument: a collection changed while it was serialized; the next save writes it.
                _log.Error(Category, $"Cannot write {_path}", ex);
            }
        }
    }

    public static string Serialize(PersistedState state) => JsonSerializer.Serialize(state, StateJson.Options);

    public static PersistedState? Deserialize(string json) => JsonSerializer.Deserialize<PersistedState>(json, StateJson.Options);

    /// <summary>The state in <paramref name="path"/>, or null when it cannot be read or parsed (an unparsable file is set aside).</summary>
    private PersistedState? TryRead(string path, bool quarantine)
    {
        try
        {
            var text = File.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(text) && Deserialize(text) is { } state)
            {
                return state;
            }

            _log.Write(Category, $"{path} is empty");
        }
        catch (JsonException ex)
        {
            _log.Error(Category, $"{path} is corrupt", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or unreadable, not corrupt: leave it in place.
            _log.Error(Category, $"Cannot read {path}", ex);
            return null;
        }

        if (quarantine)
        {
            Quarantine(path);
        }

        return null;
    }

    private void Quarantine(string path)
    {
        var target = path + CorruptSuffix + DateTimeOffset.Now.ToString(CorruptStampFormat, CultureInfo.InvariantCulture);
        try
        {
            File.Move(path, target);
            _log.Write(Category, $"Kept the unreadable state as {target}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(Category, $"Cannot set aside {path}", ex);
        }
    }
}
