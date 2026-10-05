using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Memory.Semantic.Runtime;

namespace Sunder.Package.Agent.Memory.Semantic.Rpc;

internal static class MemoryValueMapper
{
    private static readonly JsonSerializerOptions Options = CreateOptions();
    internal static T Convert<T>(object value) where T : class => MemoryValueJson.Convert<T>(value, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = MemoryValueJson.CreateOptions();
        options.Converters.Add(new Scalar<MemoryId, Guid>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new Scalar<ProfileId, string>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new Scalar<SearchText, string>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new Scalar<MemoryCategory, string>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new Scalar<MemoryContent, string>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new Scalar<EvidenceText, string>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new Scalar<CorrectionNote, string>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new Scalar<StatusText, string>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new Scalar<ProviderId, string>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new Scalar<ModelId, string>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new Scalar<ConfigurationFingerprint, string>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new Scalar<MemoryScore, float>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new Scalar<AccessCount, long>(value => new() { Value = value }, value => value.Value));
        options.Converters.Add(new Scalar<MemoryRevision, long>(value => new() { Value = value }, value => value.Value));
        return options;
    }

    private sealed class Scalar<T, TValue>(Func<TValue, T> wrap, Func<T, TValue> unwrap) : JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => wrap(JsonSerializer.Deserialize<TValue>(ref reader, options)!);
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, unwrap(value), options);
    }
}
