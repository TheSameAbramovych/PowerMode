using System.Text.Json;
using System.Text.Json.Serialization;
using PowerMode.Core.Levers;

namespace PowerMode.Core.State;

/// <summary>
/// The JSON dialect of state.json: <see cref="JsonDefaults"/> plus tolerance for files written by another version.
/// An unknown lever or mode drops only that dictionary entry or set element, and an unknown lever status reads as
/// <see cref="LeverStatus.Failed"/>, so one new enum value never discards the whole file and its baseline.
/// config.json keeps the strict dialect.
/// </summary>
public static class StateJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonDefaults.Options);
        options.Converters.Insert(0, new LenientLeverStatusConverter());
        options.Converters.Insert(0, new LenientEnumCollectionConverterFactory());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>A defined enum member by name (any case); numbers and unknown names are rejected.</summary>
    internal static bool TryParseName<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        return text is { Length: > 0 } && char.IsAsciiLetter(text[0]) && Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    internal static string Name<TEnum>(TEnum value)
        where TEnum : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private sealed class LenientLeverStatusConverter : JsonConverter<LeverStatus>
    {
        public override LeverStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && TryParseName<LeverStatus>(reader.GetString(), out var status) ? status : LeverStatus.Failed;

        public override void Write(Utf8JsonWriter writer, LeverStatus value, JsonSerializerOptions options) => writer.WriteStringValue(Name(value));
    }

    /// <summary>Dictionaries keyed by an enum and sets of an enum.</summary>
    private sealed class LenientEnumCollectionConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType
            && typeToConvert.GetGenericArguments()[0].IsEnum
            && (typeToConvert.GetGenericTypeDefinition() == typeof(Dictionary<,>) || typeToConvert.GetGenericTypeDefinition() == typeof(HashSet<>));

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var arguments = typeToConvert.GetGenericArguments();
            var converter = typeToConvert.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                ? typeof(LenientDictionaryConverter<,>).MakeGenericType(arguments)
                : typeof(LenientSetConverter<>).MakeGenericType(arguments);
            return (JsonConverter)Activator.CreateInstance(converter)!;
        }
    }

    private sealed class LenientDictionaryConverter<TKey, TValue> : JsonConverter<Dictionary<TKey, TValue>>
        where TKey : struct, Enum
    {
        public override Dictionary<TKey, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException($"Expected an object for {typeToConvert.Name}.");
            }

            var result = new Dictionary<TKey, TValue>();
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                if (TryParseName<TKey>(name, out var key) && JsonSerializer.Deserialize<TValue>(ref reader, options) is { } value)
                {
                    result[key] = value;
                }
                else
                {
                    reader.Skip();
                }
            }

            return result;
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<TKey, TValue> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var (key, item) in value)
            {
                writer.WritePropertyName(Name(key));
                JsonSerializer.Serialize(writer, item, options);
            }

            writer.WriteEndObject();
        }
    }

    private sealed class LenientSetConverter<T> : JsonConverter<HashSet<T>>
        where T : struct, Enum
    {
        public override HashSet<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException($"Expected an array for {typeToConvert.Name}.");
            }

            var result = new HashSet<T>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String && TryParseName<T>(reader.GetString(), out var item))
                {
                    result.Add(item);
                }
                else
                {
                    reader.Skip();
                }
            }

            return result;
        }

        public override void Write(Utf8JsonWriter writer, HashSet<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value.Order())
            {
                writer.WriteStringValue(Name(item));
            }

            writer.WriteEndArray();
        }
    }
}
