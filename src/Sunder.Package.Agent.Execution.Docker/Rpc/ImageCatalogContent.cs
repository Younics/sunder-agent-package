using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Execution.Docker;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class ImageCatalogContent
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    internal static async ValueTask<SunderRpcContentReference> Write(SunderRpcInvocationContext context, IReadOnlyList<DockerImageDefinition> images, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(images, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The Docker image catalog exceeds its 8 MiB content limit.");
        await using var stream = new MemoryStream(bytes, writable: false);
        var reference = await context.RegisterContentAsync(stream,
            new("application/json", "docker-image-catalog.json", bytes.LongLength, Repeatability: SunderRpcContentRepeatability.SingleUse, MaximumUses: 1), token).ConfigureAwait(false);
        Validate(reference, context.DeadlineUtc);
        if (reference.Length != bytes.LongLength || reference.Sha256 != Convert.ToHexStringLower(SHA256.HashData(bytes)))
            throw new InvalidDataException("The Host returned inconsistent Docker catalog metadata.");
        return reference;
    }

    internal static async ValueTask<IReadOnlyList<DockerImageDefinition>> Read(ISunderRpcCallScope scope, SunderRpcContentReference reference, CancellationToken token)
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
                throw new InvalidDataException("The Docker image catalog exceeds its authenticated length.");
            content.Write(buffer, 0, read);
        }
        var bytes = content.ToArray();
        if (bytes.LongLength != reference.Length || reference.Sha256 != Convert.ToHexStringLower(SHA256.HashData(bytes)))
            throw new InvalidDataException("The Docker image catalog digest or length is invalid.");
        return JsonSerializer.Deserialize<IReadOnlyList<DockerImageDefinition>>(bytes, Json)
            ?? throw new InvalidDataException("The Docker image catalog is empty.");
    }

    private static void Validate(SunderRpcContentReference reference, DateTimeOffset deadlineUtc)
    {
        if (reference.Length < 0 || reference.Length > MaximumBytes
            || !string.Equals(reference.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || reference.Repeatability != SunderRpcContentRepeatability.SingleUse
            || reference.ExpiresAtUtc <= DateTimeOffset.UtcNow || reference.ExpiresAtUtc > deadlineUtc)
            throw new InvalidDataException("The Docker image catalog content reference is invalid.");
    }
}
