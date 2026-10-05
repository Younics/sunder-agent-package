using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Builder;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class BuilderRpcPayloadLimits
{
    internal const int MaximumRequestBytes = 1024 * 1024;
    internal const int MaximumResponseBytes = 8 * 1024 * 1024;
    internal const int ResponseSafetyBytes = 64 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static int GetSerializedSize<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions).Length;

    internal static byte[] SerializeResponse(BuilderRpcResponse response)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        if (bytes.Length >= MaximumResponseBytes - ResponseSafetyBytes)
        {
            throw new InvalidDataException(
                "Package Builder RPC response exceeds its content limit.");
        }
        return bytes;
    }

    internal static async ValueTask<BuilderRpcResponse> ReadResponseAsync(
        ISunderRpcCallScope scope,
        SunderRpcContentReference reference,
        CancellationToken cancellationToken)
    {
        ValidateContentReference(reference, scope.DeadlineUtc);
        await using var stream = await scope.OpenContentAsync(reference, cancellationToken).ConfigureAwait(false);
        using var content = new MemoryStream(checked((int)reference.Length));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (content.Length + read > reference.Length
                || content.Length + read >= MaximumResponseBytes - ResponseSafetyBytes)
            {
                throw new InvalidDataException("The Builder Runtime result exceeds its authenticated length.");
            }
            content.Write(buffer, 0, read);
        }

        var bytes = content.ToArray();
        ValidateContentIntegrity(reference, bytes);
        return JsonSerializer.Deserialize<BuilderRpcResponse>(bytes, JsonOptions)
               ?? throw new InvalidDataException("The Builder Runtime returned an empty result.");
    }

    internal static void ValidateRegisteredContent(
        SunderRpcContentReference reference,
        byte[] bytes,
        DateTimeOffset deadlineUtc)
    {
        ValidateContentReference(reference, deadlineUtc);
        ValidateContentIntegrity(reference, bytes);
    }

    private static void ValidateContentReference(
        SunderRpcContentReference reference,
        DateTimeOffset deadlineUtc)
    {
        if (reference.Length < 0
            || reference.Length >= MaximumResponseBytes - ResponseSafetyBytes
            || !string.Equals(reference.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || reference.Repeatability != SunderRpcContentRepeatability.SingleUse
            || reference.ExpiresAtUtc <= DateTimeOffset.UtcNow
            || reference.ExpiresAtUtc > deadlineUtc
            || reference.Sha256.Length != 64
            || reference.Sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException("The Builder Runtime returned invalid result content metadata.");
        }
    }

    private static void ValidateContentIntegrity(
        SunderRpcContentReference reference,
        byte[] bytes)
    {
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (bytes.LongLength != reference.Length
            || !string.Equals(reference.Sha256, digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Builder Runtime result digest or length is invalid.");
        }
    }
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum BuilderRpcOperationKind
{
    ListWorkspaces,
    ResolveWorkspace,
    ExecuteProcess,
    ExecuteShell,
    MapToHostPath,
    AddPathEntry,
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record BuilderRpcRequest(
    BuilderRpcOperationKind Kind,
    string? WorkspaceId = null,
    string? FileName = null,
    IReadOnlyList<string>? Arguments = null,
    string? Command = null,
    string? WorkingDirectory = null,
    int TimeoutSeconds = 600,
    string? ExecutionPath = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record BuilderRpcResponse(
    IReadOnlyList<AgentWorkspaceRecord>? Workspaces = null,
    BuilderWorkspaceExecutionProjection? Execution = null,
    BuilderProcessResult? Process = null,
    AgentExecutionPathMapping? PathMapping = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record BuilderWorkspaceExecutionProjection(
    AgentWorkspaceRecord Workspace,
    AgentWorkspaceBindingRecord Binding,
    AgentExecutionTargetDescriptor Target,
    AgentExecutionScopeDescriptor Scope,
    AgentExecutionShellDescriptor Shell,
    bool SupportsPathMapping,
    bool SupportsPathEnvironment);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record BuilderProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool WasTruncated = false)
{
    public string CombinedOutput => (StandardOutput + Environment.NewLine + StandardError).Trim();
}

internal interface IBuilderRpcGateway
{
    ValueTask<BuilderRpcResponse> InvokeAsync(
        BuilderRpcRequest request,
        CancellationToken cancellationToken = default);
}
