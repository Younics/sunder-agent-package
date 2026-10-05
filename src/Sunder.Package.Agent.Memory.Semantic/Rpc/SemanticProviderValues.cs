using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Memory.Semantic.Rpc;

// Maps existing business records to contract-local generated values. Wire shape
// validation and dispatch belong to the imported generated bindings.
internal static class SemanticProviderValues
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new SunderRpcJsonConverterFactory(),
            new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false),
            new UtcConverter(),
        },
    };

    internal static T Convert<T>(object value) where T : class
        => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Options), Options)
            ?? throw new InvalidDataException("A semantic memory provider value could not be mapped.");

    private sealed class UtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new SunderRpcOffsetDateTime(reader.GetString()!).TryGetDateTimeOffset(out var value)
                ? value.ToUniversalTime() : throw new InvalidDataException("The memory provider timestamp exceeds .NET precision.");

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }
}
