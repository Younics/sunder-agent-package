using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Mcp.Protocol.Generated;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Mcp.Runtime;

internal static class McpManagementPayload
{
    internal const int MaximumPayloadBytes = 8 * 1024 * 1024;
    private const string JsonMediaType = "application/json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static ValueTask<ManagementContent> RegisterRequestAsync<T>(
        ISunderRpcCallScope scope,
        SunderRpcEndpointReference endpoint,
        T value,
        string fileName,
        CancellationToken cancellationToken)
        where T : notnull
        => RegisterAsync(
            value,
            fileName,
            scope.DeadlineUtc,
            (stream, options, token) => scope.RegisterContentAsync(endpoint, stream, options, token),
            cancellationToken);

    internal static ValueTask<ManagementContent> RegisterResponseAsync<T>(
        SunderRpcInvocationContext context,
        T value,
        string fileName,
        CancellationToken cancellationToken)
        where T : notnull
        => RegisterAsync(
            value,
            fileName,
            context.DeadlineUtc,
            context.RegisterContentAsync,
            cancellationToken);

    internal static async ValueTask<T> ReadRequestAsync<T>(
        SunderRpcInvocationContext context,
        ManagementContent payload,
        CancellationToken cancellationToken)
        where T : notnull
    {
        try
        {
            return await ReadAsync<T>(
                payload.Content,
                context.DeadlineUtc,
                context.OpenContentAsync,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            throw new McpManagementOperationException(
                "The MCP management request payload is invalid.",
                exception);
        }
    }

    internal static ValueTask<T> ReadResponseAsync<T>(
        ISunderRpcCallScope scope,
        ManagementContent payload,
        CancellationToken cancellationToken)
        where T : notnull
        => ReadAsync<T>(
            payload.Content,
            scope.DeadlineUtc,
            scope.OpenContentAsync,
            cancellationToken);

    private static async ValueTask<ManagementContent> RegisterAsync<T>(
        T value,
        string fileName,
        DateTimeOffset deadlineUtc,
        Func<Stream, SunderRpcContentRegistrationOptions, CancellationToken, ValueTask<SunderRpcContentReference>> register,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > MaximumPayloadBytes)
        {
            throw new InvalidDataException("The MCP management payload exceeds its 8 MiB content limit.");
        }

        await using var stream = new MemoryStream(bytes, writable: false);
        var reference = await register(
            stream,
            new SunderRpcContentRegistrationOptions(
                JsonMediaType,
                fileName,
                bytes.LongLength,
                Repeatability: SunderRpcContentRepeatability.SingleUse,
                MaximumUses: 1),
            cancellationToken).ConfigureAwait(false);
        ValidateReference(reference, deadlineUtc);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (reference.Length != bytes.LongLength
            || !string.Equals(reference.Sha256, digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Host returned inconsistent MCP content metadata.");
        }
        return new ManagementContent { Content = reference };
    }

    private static async ValueTask<T> ReadAsync<T>(
        SunderRpcContentReference reference,
        DateTimeOffset deadlineUtc,
        Func<SunderRpcContentReference, CancellationToken, ValueTask<Stream>> open,
        CancellationToken cancellationToken)
        where T : notnull
    {
        ValidateReference(reference, deadlineUtc);
        await using var stream = await open(reference, cancellationToken).ConfigureAwait(false);
        using var content = new MemoryStream(checked((int)reference.Length));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (content.Length + read > reference.Length || content.Length + read > MaximumPayloadBytes)
            {
                throw new InvalidDataException("The MCP content exceeds its authenticated length.");
            }
            content.Write(buffer, 0, read);
        }

        var bytes = content.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (bytes.LongLength != reference.Length
            || !string.Equals(reference.Sha256, digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The MCP content digest or length is invalid.");
        }
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
               ?? throw new InvalidDataException("The MCP management payload is empty.");
    }

    private static void ValidateReference(
        SunderRpcContentReference reference,
        DateTimeOffset deadlineUtc)
    {
        if (reference.Length < 0
            || reference.Length > MaximumPayloadBytes
            || !string.Equals(reference.MediaType, JsonMediaType, StringComparison.OrdinalIgnoreCase)
            || reference.Repeatability != SunderRpcContentRepeatability.SingleUse
            || reference.ExpiresAtUtc <= DateTimeOffset.UtcNow
            || reference.ExpiresAtUtc > deadlineUtc
            || reference.Sha256.Length != 64
            || reference.Sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException("The MCP content reference metadata is invalid.");
        }
    }
}
