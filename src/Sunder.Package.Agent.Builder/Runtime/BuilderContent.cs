using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Builder;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class BuilderContent
{
    internal const int MaximumRequestBytes = 64 * 1024;
    internal const int MaximumResponseBytes = 8 * 1024 * 1024;
    internal const int ResponseSafetyBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        Converters = { new SunderRpcJsonConverterFactory() },
    };

    internal static string Serialize<T>(T value)
    {
        ValidateRequest(value);
        return JsonSerializer.Serialize(value, Json);
    }

    internal static BuilderJobRequest ReadJobRequest(string value)
    {
        if (value is null || value.Length >= MaximumRequestBytes || Encoding.UTF8.GetByteCount(value) >= MaximumRequestBytes)
            throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "agent.builder.request-too-large", "Builder job request exceeds its payload limit."));
        // Duplicate JSON keys would make retry identity and UI intent ambiguous.
        using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 });
        CheckKeys(document.RootElement);
        return JsonSerializer.Deserialize<BuilderJobRequest>(document.RootElement, Json)
            ?? throw new ArgumentException("Builder job request is empty.");
    }

    private static void CheckKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("Builder request contains duplicate or case-colliding properties.");
                CheckKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckKeys(item);
    }

    internal static void ValidateRequest<T>(T value)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(value, Json).Length >= MaximumRequestBytes)
            throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "agent.builder.request-too-large", "Sunder Package Maker request exceeds its payload limit."));
    }

    internal static SunderContent Create<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length >= MaximumResponseBytes - ResponseSafetyBytes)
            throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "agent.builder.response-too-large", "Sunder Package Maker result exceeds its content limit."));
        return SunderContent.FromBytes(bytes, "application/json", "builder-result.json");
    }

    internal static async ValueTask<T> ReadAsync<T>(ValueTask<SunderContent> result, CancellationToken token) where T : class
    {
        await using var content = await result.ConfigureAwait(false);
        if (content.Length is not { } length || length is < 0 or >= MaximumResponseBytes - ResponseSafetyBytes
            || !string.Equals(content.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || content.Sha256 is not { Length: 64 } digest
            || digest.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || content.ExpiresAtUtc is { } expires && expires <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("The Sunder Package Maker result has invalid content metadata.");
        await using var stream = await content.OpenReadAsync(token).ConfigureAwait(false);
        var bytes = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0
            || !string.Equals(digest, Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.Ordinal))
            throw new InvalidDataException("The Sunder Package Maker result digest or length is invalid.");
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Sunder Package Maker returned an empty result.");
    }
}
