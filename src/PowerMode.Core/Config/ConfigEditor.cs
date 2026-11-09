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
    /// Write the keys whose values differ between <paramref name="before"/> and <paramref name="after"/> (nested ones
    /// like <c>modes.station.scheme.maxProcessorPercent</c> too). Missing objects on the way are created; existing keys
    /// are matched case-insensitively and keep their spelling; keys that did not change are not touched.
    /// </summary>
    /// <exception cref="JsonException">The text is not a JSON object.</exception>
    public static string SetChanges(string json, AppConfig before, AppConfig after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (JsonNode.Parse(json, documentOptions: DocumentOptions) is not JsonObject root)
        {
            throw new JsonException("The config root must be a JSON object.");
        }

        WriteChanges(root, Serialize(before), Serialize(after));
        return root.ToJsonString(JsonDefaults.Options);
    }

    private static JsonObject Serialize(AppConfig config) => JsonSerializer.SerializeToNode(config, JsonDefaults.Options)!.AsObject();

    private static void WriteChanges(JsonObject target, JsonObject before, JsonObject after)
    {
        foreach (var (key, value) in after)
        {
            var old = before[key];
            if (JsonNode.DeepEquals(old, value))
            {
                continue;
            }

            if (value is JsonObject nested && old is JsonObject oldNested)
            {
                WriteChanges(Child(target, key), oldNested, nested);
            }
            else
            {
                target[KeyOf(target, key)] = value?.DeepClone();
            }
        }
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

    private static string KeyOf(JsonObject parent, string key) =>
        parent.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
}
