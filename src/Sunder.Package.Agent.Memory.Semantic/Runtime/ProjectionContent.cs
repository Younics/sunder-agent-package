using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Memory.Semantic.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class ProjectionContent
{
    internal const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new SunderRpcJsonConverterFactory(), new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    internal static SunderContent Create(MemoryProjection projection)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(projection, Json);
        if (bytes.Length > MaximumBytes)
            throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "memory.semantic.operation-failed", "The semantic memory projection exceeds its 16 MiB content limit."));
        return SunderContent.FromBytes(bytes, "application/json", "memory-projection.json");
    }

    internal static async ValueTask<MemoryProjection> Read(ValueTask<SunderContent> result, CancellationToken token)
    {
        await using var reference = await result.ConfigureAwait(false);
        Validate(reference);
        await using var stream = await reference.OpenReadAsync(token).ConfigureAwait(false);
        using var content = new MemoryStream(checked((int)reference.Length!.Value));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (content.Length + read > reference.Length || content.Length + read > MaximumBytes)
                throw new InvalidDataException("The memory projection exceeds its authenticated length.");
            content.Write(buffer, 0, read);
        }
        var bytes = content.ToArray();
        if (bytes.LongLength != reference.Length || reference.Sha256 != Convert.ToHexStringLower(SHA256.HashData(bytes)))
            throw new InvalidDataException("The memory projection digest or length is invalid.");
        return JsonSerializer.Deserialize<MemoryProjection>(bytes, Json)
            ?? throw new InvalidDataException("The semantic memory projection is empty.");
    }

    private static void Validate(SunderContent reference)
    {
        if (reference.Length is null or < 0 or > MaximumBytes || reference.Sha256 is not { Length: 64 }
            || !string.Equals(reference.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || reference.ExpiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("The semantic memory content reference is invalid.");
    }
}
