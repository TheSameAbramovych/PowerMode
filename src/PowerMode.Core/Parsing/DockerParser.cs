using System.Text.Json;

namespace PowerMode.Core.Parsing;

public enum DockerEngineStatus
{
    Unknown,
    Running,
    Stopped,
    Starting,
}

public static class DockerParser
{
    private const string StatusKey = "Status";

    /// <summary><c>docker ps --format {{.Names}}</c> → container names (one per line).</summary>
    public static IReadOnlyList<string> ParseNames(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return [.. output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal)];
    }

    /// <summary><c>docker desktop status --format json</c> (or the plain table) → engine status.</summary>
    public static DockerEngineStatus ParseDesktopStatus(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var text = output.Trim();
        string? status = null;
        if (text.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                status = document.RootElement.EnumerateObject()
                    .FirstOrDefault(p => string.Equals(p.Name, StatusKey, StringComparison.OrdinalIgnoreCase))
                    .Value.ToString();
            }
            catch (JsonException)
            {
                status = null;
            }
        }
        else
        {
            var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(StatusKey, StringComparison.OrdinalIgnoreCase));
            status = line?[StatusKey.Length..].Trim();
        }

        return status?.ToLowerInvariant() switch
        {
            "running" => DockerEngineStatus.Running,
            "stopped" or "paused" => DockerEngineStatus.Stopped,
            "starting" => DockerEngineStatus.Starting,
            _ => DockerEngineStatus.Unknown,
        };
    }
}
