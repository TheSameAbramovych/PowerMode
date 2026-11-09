using System.Text.Json;
using System.Text.Json.Nodes;
using PowerMode.Core.Input;
using PowerMode.Core.Model;

namespace PowerMode.Core.Config;

public sealed record ConfigParseResult(AppConfig? Config, IReadOnlyList<string> Errors)
{
    public bool IsValid => Config is not null && Errors.Count == 0;
}

/// <summary>
/// Parses config.json on top of the defaults: every key the user omits keeps its default (objects are merged
/// key by key, arrays and values replace). Out-of-range values reject the whole file so the caller can keep
/// the last good config.
/// </summary>
public static class ConfigLoader
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Serialize(AppConfig config) => JsonSerializer.Serialize(config, JsonDefaults.Options);

    public static ConfigParseResult Parse(string json)
    {
        JsonNode? user;
        try
        {
            user = JsonNode.Parse(json, documentOptions: DocumentOptions);
        }
        catch (JsonException ex)
        {
            return new ConfigParseResult(null, [$"Invalid JSON: {ex.Message}"]);
        }

        if (user is not JsonObject userObject)
        {
            return new ConfigParseResult(null, ["The config root must be a JSON object."]);
        }

        var merged = JsonSerializer.SerializeToNode(AppConfig.CreateDefault(), JsonDefaults.Options)!.AsObject();
        Merge(merged, userObject);

        AppConfig? config;
        try
        {
            config = merged.Deserialize<AppConfig>(ConfigOptions);
        }
        catch (JsonException ex)
        {
            return new ConfigParseResult(null, [$"Invalid value: {ex.Message}"]);
        }

        if (config is null)
        {
            return new ConfigParseResult(null, ["The config is empty."]);
        }

        try
        {
            var errors = Validate(config);
            return errors.Count == 0 ? new ConfigParseResult(config, []) : new ConfigParseResult(null, errors);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Last resort: a config that breaks validation is invalid, never a crash of the caller (a timer thread on reload).
            return new ConfigParseResult(null, [$"Invalid config: {ex.Message}"]);
        }
    }

    /// <summary>
    /// The JSON dialect plus nullability: an explicit <c>null</c> for an object, list or text value (e.g.
    /// <c>"modes": {"station": null}</c>) makes the file invalid instead of replacing the default. Numbers that are
    /// nullable in the schema (brightness, refresh rate, GPU limit) still take <c>null</c> to mean "leave alone".
    /// state.json keeps its own lenient dialect.
    /// </summary>
    private static readonly JsonSerializerOptions ConfigOptions = CreateConfigOptions();

    private static JsonSerializerOptions CreateConfigOptions()
    {
        var options = new JsonSerializerOptions(JsonDefaults.Options) { RespectNullableAnnotations = true };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>What nullability annotations do not cover: null or blank list entries and null relaunch arguments.</summary>
    private static void ValidateEntries(List<string> errors, AppConfig config)
    {
        foreach (var mode in new[] { PowerModeKind.Station, PowerModeKind.Reserve })
        {
            var m = config.Modes.For(mode)!;
            var prefix = $"modes.{ModeNames.Key(mode)}";
            RequireNames(errors, $"{prefix}.closeApps", m.CloseApps);
            RequireNames(errors, $"{prefix}.efficiencyApps", m.EfficiencyApps);
            RequireNames(errors, $"{prefix}.stopServices", m.StopServices);
        }

        RequireNames(errors, "docker.excludedContainers", config.Docker.ExcludedContainers);
        foreach (var (name, arguments) in config.RelaunchArguments)
        {
            if (string.IsNullOrWhiteSpace(name) || arguments is null)
            {
                errors.Add($"relaunchArguments.{name}: needs a process name and an arguments string (use \"\" for none).");
            }
        }
    }

    private static void RequireNames(List<string> errors, string name, List<string> values)
    {
        if (values.Exists(string.IsNullOrWhiteSpace))
        {
            errors.Add($"{name} must not contain null or empty names.");
        }
    }

    internal static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            var existingKey = target.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
            if (value is JsonObject sourceChild && target[existingKey] is JsonObject targetChild)
            {
                Merge(targetChild, sourceChild);
            }
            else
            {
                target[existingKey] = value?.DeepClone();
            }
        }
    }

    public static IReadOnlyList<string> Validate(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<string>();
        ValidateEntries(errors, config);
        foreach (var mode in new[] { PowerModeKind.Station, PowerModeKind.Reserve })
        {
            var m = config.Modes.For(mode)!;
            var prefix = $"modes.{ModeNames.Key(mode)}";
            RequireRange(errors, $"{prefix}.brightnessPercent", m.BrightnessPercent, 0, 100);
            RequireRange(errors, $"{prefix}.refreshRateHz", m.RefreshRateHz, 1, 1000);
            RequireRange(errors, $"{prefix}.gpuLimitWatts", m.GpuLimitWatts, 1, 2000);
            RequireRange(errors, $"{prefix}.scheme.maxProcessorPercent", m.Scheme.MaxProcessorPercent, 0, 100);
            RequireRange(errors, $"{prefix}.scheme.minProcessorPercent", m.Scheme.MinProcessorPercent, 0, 100);
            RequireRange(errors, $"{prefix}.scheme.displayOffSeconds", m.Scheme.DisplayOffSeconds, 0, int.MaxValue);
            RequireRange(errors, $"{prefix}.scheme.sleepSeconds", m.Scheme.SleepSeconds, 0, int.MaxValue);
            RequireRange(errors, $"{prefix}.scheme.hibernateSeconds", m.Scheme.HibernateSeconds, 0, int.MaxValue);
            if (m.Scheme.MinProcessorPercent > m.Scheme.MaxProcessorPercent)
            {
                errors.Add($"{prefix}.scheme.minProcessorPercent must not exceed maxProcessorPercent.");
            }

            if (m.Scheme.Id == Guid.Empty || m.Scheme.SourceGuid == Guid.Empty || m.Scheme.Id == m.Scheme.SourceGuid)
            {
                errors.Add($"{prefix}.Scheme.Id and sourceGuid must be two different non-empty GUIDs.");
            }
        }

        if (config.Modes.Station.Scheme.Id == config.Modes.Reserve.Scheme.Id)
        {
            errors.Add("modes.station.Scheme.Id and modes.reserve.Scheme.Id must differ.");
        }

        var fallback = config.FallbackScheme;
        if (fallback == Guid.Empty || PowerSchemes.IsOwn(fallback) || fallback == config.Modes.Station.Scheme.Id || fallback == config.Modes.Reserve.Scheme.Id)
        {
            errors.Add("fallbackScheme must be a non-empty GUID other than the two PowerMode schemes.");
        }

        RequireRange(errors, "docker.engineWaitSeconds", config.Docker.EngineWaitSeconds, 1, 900);
        RequireRange(errors, "flyout.margin", config.Flyout.Margin, 0, 200);
        foreach (var mode in ModeNames.All)
        {
            var text = config.Hotkeys.For(mode);
            if (!string.IsNullOrWhiteSpace(text) && !Hotkey.TryParse(text, out _))
            {
                errors.Add($"hotkeys.{ModeNames.Key(mode)}: cannot parse \"{text}\".");
            }
        }

        return errors;
    }

    private static void RequireRange(List<string> errors, string name, int? value, int min, int max)
    {
        if (value is { } v && (v < min || v > max))
        {
            errors.Add($"{name} must be between {min} and {max} (was {v}).");
        }
    }
}
