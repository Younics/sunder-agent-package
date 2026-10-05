using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Runtime.Rpc;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class AgentConsumerValues
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Generated, already validated union values do not require the discriminator
        // to be the first JSON member. Preserve that order-independent wire contract.
        AllowOutOfOrderMetadataProperties = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new SunderRpcJsonConverterFactory(), new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false), new UtcConverter() },
    };

    internal static T Convert<T>(object value) where T : class
        => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Json), Json)
            ?? throw new InvalidDataException("An Agent consumer value could not be mapped.");

    internal static T Convert<TSource, T>(TSource value) where T : class
        => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, Json), Json)
            ?? throw new InvalidDataException("An Agent consumer value could not be mapped.");

    private sealed class UtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDateTimeOffset();
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }
}
