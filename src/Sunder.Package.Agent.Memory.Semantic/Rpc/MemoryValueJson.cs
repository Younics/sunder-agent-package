using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Memory.Semantic.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class MemoryValueJson
{
    internal static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(StoredMemoryRecord))
                info.Properties.Single(property => property.Name == "state").CustomConverter = new StateConverter();
        });
        return new(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = resolver,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new SunderRpcJsonConverterFactory(), new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false), new UtcConverter() },
        };
    }

    internal static T Convert<T>(object value, JsonSerializerOptions options) where T : class
        => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), options), options)
            ?? throw new InvalidDataException("A semantic memory value could not be mapped.");

    private sealed class UtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new SunderRpcOffsetDateTime(reader.GetString()!).TryGetDateTimeOffset(out var value)
                ? value.ToUniversalTime() : throw new InvalidDataException("The memory timestamp exceeds .NET precision.");
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }

    private sealed class StateConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetString() switch
            {
                "active" => MemoryRecordStates.Active,
                "contested" => MemoryRecordStates.Contested,
                "forgotten" => MemoryRecordStates.Forgotten,
                "superseded" => MemoryRecordStates.Superseded,
                _ => throw new InvalidDataException("The memory state is invalid."),
            };
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value switch
            {
                MemoryRecordStates.Active => "active",
                MemoryRecordStates.Contested => "contested",
                MemoryRecordStates.Forgotten => "forgotten",
                MemoryRecordStates.Superseded => "superseded",
                _ => throw new InvalidDataException("The memory state is invalid."),
            });
    }
}
