using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Owner;

// Retains the owner port's existing numeric-enum, UTC and omitted-null domain
// projection. The closed scalar converters adapt ordinary source value wrappers;
// descriptor generation and RPC encoding remain owned by Sunder.Package.Build.
internal static class OwnerValueMapper
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static T Convert<T>(object value) where T : class
        => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Options), Options)
           ?? throw new InvalidOperationException("An Agent owner value could not be mapped.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new SunderRpcJsonConverterFactory());
        options.Converters.Add(new ScalarConverter<Uuid, Guid>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new ScalarConverter<Timestamp, SunderRpcOffsetDateTime>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new ScalarConverter<ModelReleaseDate, DateOnly>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new ScalarConverter<JsonText, string>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new ScalarConverter<ContentReference, SunderRpcContentReference>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new ScalarConverter<JSONObject, JsonElement>(value => new() { Value = value }, value => value.Value));
        return options;
    }

    private sealed class ScalarConverter<T, TValue>(Func<TValue, T> wrap, Func<T, TValue> unwrap) : JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => wrap(JsonSerializer.Deserialize<TValue>(ref reader, options)!);
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, unwrap(value), options);
    }
}
