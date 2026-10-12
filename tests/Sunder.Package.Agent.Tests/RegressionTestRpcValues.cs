using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Tests;

// JSON snapshots for data assertions and deliberately malformed broker inputs.
// Natural requests, service references, resources and transient authority must
// cross the actual generated client/handler codecs, never a fixture projection.
internal static class RegressionTestRpcValues
{
    internal static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new SunderRpcJsonConverterFactory(), new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false), new UtcConverter() },
    };

    internal static T Convert<T>(object value) where T : class
        => JsonSerializer.SerializeToElement(value, value.GetType(), Json).Deserialize<T>(Json)
            ?? throw new InvalidDataException($"A test RPC value could not be projected to {typeof(T)}.");

    internal static T? Optional<T>(object? value) where T : class => value is null ? null : Convert<T>(value);

    internal static string Serialize(object value) => JsonSerializer.Serialize(value, value.GetType(), Json);

    private sealed class UtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetDateTimeOffset();
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }
}
