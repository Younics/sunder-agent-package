using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Provider.LMStudio.Rpc;

internal static class LMStudioProviderValues
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new SunderRpcJsonConverterFactory(), new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false), new UtcConverter() },
    };

    internal static T Convert<T>(object value, Type? declaredType = null) where T : class
        => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, declaredType ?? value.GetType(), Json), Json)
            ?? throw new InvalidDataException("An LM Studio provider value could not be mapped.");

    internal static T? Optional<T>(object? value) where T : class => value is null ? null : Convert<T>(value);

    private sealed class UtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new SunderRpcOffsetDateTime(reader.GetString()!).TryGetDateTimeOffset(out var value)
                ? value.ToUniversalTime() : throw new InvalidDataException("The LM Studio timestamp exceeds .NET precision.");
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }
}
