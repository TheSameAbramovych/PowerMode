using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerMode.Core.Config;

/// <summary>
/// Changes single keys of config.json text and leaves every other key as it is. The result is written in the dialect
/// the app writes (indented, camelCase); comments in the file do not survive an edit.
/// </summary>
public static class ConfigEditor
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Set <c>closeApps</c>, <c>efficiencyApps</c> and <c>pauseWallpaperEngine</c> of one saving mode. Missing objects on
    /// the way are created; existing keys are matched case-insensitively and keep their spelling.
    /// </summary>
    /// <exception cref="JsonException">The text is not a JSON object.</exception>
    public static string SetModeApps(string json, PowerModeKind mode, ModeConfig values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (JsonNode.Parse(json, documentOptions: DocumentOptions) is not JsonObject root)
        {
            throw new JsonException("The config root must be a JSON object.");
        }

        var modeNode = Child(Child(root, "modes"), ModeNames.Key(mode));
        Set(modeNode, "closeApps", JsonSerializer.SerializeToNode(values.CloseApps, JsonDefaults.Options));
        Set(modeNode, "efficiencyApps", JsonSerializer.SerializeToNode(values.EfficiencyApps, JsonDefaults.Options));
        Set(modeNode, "pauseWallpaperEngine", JsonValue.Create(values.PauseWallpaperEngine));
        return root.ToJsonString(JsonDefaults.Options);
    }

    private static JsonObject Child(JsonObject parent, string key)
    {
        if (parent[KeyOf(parent, key)] is JsonObject child)
        {
            return child;
        }

        var created = new JsonObject();
        parent[KeyOf(parent, key)] = created;
        return created;
    }

    private static void Set(JsonObject parent, string key, JsonNode? value) => parent[KeyOf(parent, key)] = value;

    private static string KeyOf(JsonObject parent, string key) =>
        parent.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
}
