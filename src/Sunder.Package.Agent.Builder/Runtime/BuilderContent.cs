using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentModels = Sunder.Generated.Packages.Sunder_Package_Agent_Models;

namespace Sunder.Package.Agent.Builder;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class BuilderContent
{
    internal const int MaximumRequestBytes = 1024 * 1024;
    internal const int MaximumResponseBytes = 8 * 1024 * 1024;
    internal const int ResponseSafetyBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new SunderRpcJsonConverterFactory() },
    };

    internal static void ValidateRequest<T>(T value)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(value, Json).Length >= MaximumRequestBytes)
            throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "agent.builder.request-too-large", "Package Builder request exceeds its payload limit."));
    }

    internal static SunderContent Create<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length >= MaximumResponseBytes - ResponseSafetyBytes)
            throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "agent.builder.response-too-large", "Package Builder result exceeds its content limit."));
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
            throw new InvalidDataException("The Builder result has invalid content metadata.");
        await using var stream = await content.OpenReadAsync(token).ConfigureAwait(false);
        var bytes = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0
            || !string.Equals(digest, Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.Ordinal))
            throw new InvalidDataException("The Builder result digest or length is invalid.");
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("The Builder returned an empty result.");
    }
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record BuilderWorkspaceExecutionProjection(
    AgentModels.AgentWorkspaceRecord Workspace,
    AgentModels.AgentWorkspaceBindingRecord Binding,
    AgentModels.AgentExecutionTargetDescriptor Target,
    AgentModels.AgentExecutionScopeDescriptor Scope,
    AgentModels.AgentExecutionShellDescriptor Shell,
    bool SupportsPathMapping,
    bool SupportsPathEnvironment);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record BuilderProcessResult(int ExitCode, string StandardOutput, string StandardError, bool WasTruncated = false)
{
    public string CombinedOutput => (StandardOutput + Environment.NewLine + StandardError).Trim();
}
