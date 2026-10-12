using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Mcp.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class McpManagementContent
{
    internal const int MaximumPayloadBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static SunderContent Create<T>(T value, string fileName) where T : notnull
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumPayloadBytes) throw new InvalidDataException("The MCP management payload exceeds its 8 MiB content limit.");
        return SunderContent.FromBytes(bytes, "application/json", fileName);
    }

    internal static async ValueTask<T> ReadRequestAsync<T>(SunderContent content, CancellationToken token) where T : notnull
    {
        try
        {
            return JsonSerializer.Deserialize<T>(await ReadBytesAsync(content, token).ConfigureAwait(false), Json)
                ?? throw new InvalidDataException("The MCP management payload is empty.");
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        { throw new McpManagementOperationException("The MCP management request payload is invalid.", error); }
    }

    internal static async ValueTask<T> ReadResponseAsync<T>(ValueTask<SunderContent> result, CancellationToken token) where T : notnull
    {
        await using var content = await result.ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(await ReadBytesAsync(content, token).ConfigureAwait(false), Json)
            ?? throw new InvalidDataException("The MCP management payload is empty.");
    }

    internal static async ValueTask<string> ReadDocumentAsync(SunderContent content, CancellationToken token)
    {
        try { return new UTF8Encoding(false, true).GetString(await ReadBytesAsync(content, token).ConfigureAwait(false)); }
        catch (Exception error) when (error is InvalidDataException or DecoderFallbackException)
        { throw new McpManagementOperationException("The MCP configuration document is invalid.", error); }
    }

    private static async ValueTask<byte[]> ReadBytesAsync(SunderContent content, CancellationToken token)
    {
        if (content.Length is not { } length || length is < 0 or > MaximumPayloadBytes
            || !string.Equals(content.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || content.Sha256 is not { Length: 64 } digest
            || digest.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || content.ExpiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("The MCP content metadata is invalid.");
        await using var stream = await content.OpenReadAsync(token).ConfigureAwait(false);
        var bytes = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0
            || !string.Equals(digest, Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.Ordinal))
            throw new InvalidDataException("The MCP content digest or length is invalid.");
        return bytes;
    }
}
