using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

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
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    internal static async ValueTask<SunderRpcContentReference> Write(SunderRpcInvocationContext context, MemoryProjection projection, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(projection, Json);
        if (bytes.Length > MaximumBytes)
            throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "memory.semantic.operation-failed", "The semantic memory projection exceeds its 16 MiB content limit."));
        await using var stream = new MemoryStream(bytes, writable: false);
        var reference = await context.RegisterContentAsync(stream,
            new("application/json", "memory-projection.json", bytes.LongLength, Repeatability: SunderRpcContentRepeatability.SingleUse, MaximumUses: 1), token).ConfigureAwait(false);
        Validate(reference, context.DeadlineUtc);
        if (reference.Length != bytes.LongLength || reference.Sha256 != Convert.ToHexStringLower(SHA256.HashData(bytes)))
            throw new InvalidDataException("The Host returned inconsistent memory content metadata.");
        return reference;
    }

    internal static async ValueTask<MemoryProjection> Read(ISunderRpcCallScope scope, SunderRpcContentReference reference, CancellationToken token)
    {
        Validate(reference, scope.DeadlineUtc);
        await using var stream = await scope.OpenContentAsync(reference, token).ConfigureAwait(false);
        using var content = new MemoryStream(checked((int)reference.Length));
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

    private static void Validate(SunderRpcContentReference reference, DateTimeOffset deadlineUtc)
    {
        if (reference.Length < 0 || reference.Length > MaximumBytes
            || !string.Equals(reference.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || reference.Repeatability != SunderRpcContentRepeatability.SingleUse
            || reference.ExpiresAtUtc <= DateTimeOffset.UtcNow || reference.ExpiresAtUtc > deadlineUtc)
            throw new InvalidDataException("The semantic memory content reference is invalid.");
    }
}
