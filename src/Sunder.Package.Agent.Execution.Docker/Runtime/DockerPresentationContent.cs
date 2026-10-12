using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Execution.Docker;

/// <summary>The bounded Docker catalog document shared by its Runtime and settings UI.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class DockerPresentationContent
{
    internal const int MaximumImageBytes = 8 * 1024 * 1024;
    private const int MaximumDocumentBytes = MaximumImageBytes + 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    internal static SunderContent Create(DockerExecutionOperationResponse response)
    {
        if (response.Images is { } images && JsonSerializer.SerializeToUtf8Bytes(images, Json).Length > MaximumImageBytes)
            throw new InvalidDataException("The Docker image catalog exceeds its 8 MiB content limit.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, Json);
        if (bytes.Length > MaximumDocumentBytes) throw new InvalidDataException("The Docker catalog document exceeds its content limit.");
        return SunderContent.FromBytes(bytes, "application/json", "docker-catalog.json");
    }

    internal static async ValueTask<DockerExecutionOperationResponse> ReadAsync(SunderContent content, CancellationToken token)
    {
        if (content.Length is not { } length || length < 0 || length > MaximumDocumentBytes
            || !string.Equals(content.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Docker catalog document metadata is invalid.");
        await using var source = await content.OpenReadAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(checked((int)length));
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(chunk, token).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > length) throw new InvalidDataException("The Docker catalog exceeds its authenticated length.");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        if (bytes.LongLength != length || content.Sha256 is { } digest && digest != Convert.ToHexStringLower(SHA256.HashData(bytes)))
            throw new InvalidDataException("The Docker catalog digest or length is invalid.");
        var response = JsonSerializer.Deserialize<DockerExecutionOperationResponse>(bytes, Json)
            ?? throw new InvalidDataException("The Docker catalog document is empty.");
        if (response.Images is { } images && JsonSerializer.SerializeToUtf8Bytes(images, Json).Length > MaximumImageBytes)
            throw new InvalidDataException("The Docker image catalog exceeds its 8 MiB content limit.");
        return response;
    }
}
