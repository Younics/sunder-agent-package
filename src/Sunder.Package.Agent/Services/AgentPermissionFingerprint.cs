using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services;

internal static class AgentPermissionFingerprint
{
    private const string Version = "agent-permission-fingerprint-v2";
    private const int SnapshotVersion = 2;
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    public static string Create(
        Guid runId,
        long runRevision,
        AgentToolDescriptor descriptor,
        string callId,
        string argumentsJson,
        AgentWorkspaceRecord? workspace,
        AgentWorkspaceBindingRecord? binding,
        AgentExecutionTargetDescriptor? target,
        AgentPermissionRequest? permissionRequest,
        AgentProfileRecord? profile = null,
        string? providerId = null,
        string? modelId = null,
        string? toolOwnerPackageId = null,
        string? executionTargetOwnerPackageId = null,
        string? executionTargetConfigurationGeneration = null)
    {
        var values = new[]
        {
            Version,
            runId.ToString("N"),
            runRevision.ToString(CultureInfo.InvariantCulture),
            NormalizeIdentity(descriptor.ToolId),
            NormalizeIdentity(descriptor.SourceKind),
            NormalizeIdentity(descriptor.SourceId),
            callId.Trim(),
            NormalizeJson(argumentsJson),
            NormalizeIdentity(workspace?.WorkspaceId),
            NormalizeIdentity(binding?.BindingId),
            NormalizeIdentity(binding?.ExtensionPointId),
            NormalizeIdentity(binding?.ContributionId),
            NormalizeIdentity(binding?.Role),
            NormalizeIdentity(target?.TargetKind),
            NormalizeIdentity(target?.TargetId),
            NormalizeIdentity(toolOwnerPackageId),
            NormalizeIdentity(executionTargetOwnerPackageId),
            NormalizeResource(executionTargetConfigurationGeneration),
            NormalizeIdentity(permissionRequest?.ActionId),
            NormalizeIdentity(permissionRequest?.BoundaryId),
            NormalizeIdentity(permissionRequest?.WorkspaceId),
            NormalizeIdentity(permissionRequest?.BindingId),
            NormalizeResource(permissionRequest?.ResourceReference),
            NormalizeResourceList(permissionRequest?.ResourceReferences),
            NormalizeResourceClaims(permissionRequest?.ResourceClaims),
            NormalizeResource(permissionRequest?.Path),
            NormalizeResource(permissionRequest?.Command),
            NormalizeJson(CreateExecutionSnapshot(
                runId,
                runRevision,
                descriptor,
                callId,
                argumentsJson,
                workspace,
                binding,
                target,
                permissionRequest,
                profile,
                providerId,
                modelId,
                toolOwnerPackageId,
                executionTargetOwnerPackageId,
                executionTargetConfigurationGeneration: executionTargetConfigurationGeneration)),
        };

        var material = new StringBuilder();
        foreach (var value in values)
        {
            material.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            material.Append(':');
            material.Append(value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()))).ToLowerInvariant();
    }

    public static string CreateExecutionSnapshot(
        Guid runId,
        long runRevision,
        AgentToolDescriptor descriptor,
        string callId,
        string argumentsJson,
        AgentWorkspaceRecord? workspace,
        AgentWorkspaceBindingRecord? binding,
        AgentExecutionTargetDescriptor? target,
        AgentPermissionRequest? permissionRequest,
        AgentProfileRecord? profile,
        string? providerId,
        string? modelId,
        string? toolOwnerPackageId = null,
        string? executionTargetOwnerPackageId = null,
        AgentPermissionEvaluation? permissionEvaluation = null,
        string? executionTargetConfigurationGeneration = null)
        => JsonSerializer.Serialize(
            new PermissionExecutionSnapshot(
                SnapshotVersion,
                runId,
                runRevision,
                callId,
                NormalizeJson(argumentsJson),
                profile,
                providerId,
                modelId,
                workspace,
                binding,
                target,
                executionTargetConfigurationGeneration,
                descriptor,
                permissionRequest,
                toolOwnerPackageId,
                executionTargetOwnerPackageId,
                permissionEvaluation is null
                    ? null
                    : new PermissionDecisionAudit(
                        permissionEvaluation.Decision,
                        permissionEvaluation.BaseDecision,
                        permissionEvaluation.Source,
                        permissionEvaluation.Reason,
                        permissionEvaluation.SourceSessionId,
                        permissionEvaluation.Override?.UpdatedAtUtc)),
            SnapshotJsonOptions);

    public static bool MatchesExecutionContext(
        string snapshotJson,
        AgentPendingPermissionRequestRecord pending,
        AgentProfileRecord profile,
        string? providerId,
        string? modelId,
        AgentWorkspaceRecord workspace,
        AgentWorkspaceBindingRecord? binding)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson))
        {
            return false;
        }
        try
        {
            var snapshot = JsonSerializer.Deserialize<PermissionExecutionSnapshot>(snapshotJson, SnapshotJsonOptions);
            return snapshot is not null
                   && snapshot.Version == SnapshotVersion
                   && snapshot.RunId == pending.RunId
                   && snapshot.RunRevision == pending.RunRevision
                   && string.Equals(snapshot.CallId, pending.CallId, StringComparison.Ordinal)
                   && string.Equals(snapshot.ArgumentsJson, NormalizeJson(pending.ArgumentsJson), StringComparison.Ordinal)
                   && JsonEquals(snapshot.Profile, profile)
                   && string.Equals(snapshot.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(snapshot.ModelId, modelId, StringComparison.OrdinalIgnoreCase)
                   && JsonEquals(snapshot.Workspace, workspace)
                   && JsonEquals(snapshot.Binding, binding);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryReadExecutionSnapshot(
        string snapshotJson,
        out AgentToolDescriptor? descriptor,
        out string? executionTargetConfigurationGeneration)
    {
        descriptor = null;
        executionTargetConfigurationGeneration = null;
        if (string.IsNullOrWhiteSpace(snapshotJson))
        {
            return false;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<PermissionExecutionSnapshot>(snapshotJson, SnapshotJsonOptions);
            if (snapshot is null || snapshot.Version != SnapshotVersion || snapshot.Descriptor is null)
            {
                return false;
            }

            descriptor = snapshot.Descriptor;
            executionTargetConfigurationGeneration = snapshot.ExecutionTargetConfigurationGeneration;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool JsonEquals<T>(T left, T right)
        => string.Equals(
            NormalizeJson(JsonSerializer.Serialize(left, SnapshotJsonOptions)),
            NormalizeJson(JsonSerializer.Serialize(right, SnapshotJsonOptions)),
            StringComparison.Ordinal);

    private static string NormalizeIdentity(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static string NormalizeResource(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeResourceList(IReadOnlyList<string>? values)
        => values is null
            ? string.Empty
            : string.Join('\n', values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));

    private static string NormalizeResourceClaims(IReadOnlyList<AgentResourceClaim>? claims)
        => claims is null || claims.Count == 0
            ? string.Empty
            : NormalizeJson(JsonSerializer.Serialize(
                claims
                    .OrderBy(static claim => claim.ResourceIndex)
                    .ThenBy(static claim => claim.NamespaceId, StringComparer.Ordinal)
                    .ThenBy(static claim => claim.LogicalPath, StringComparer.Ordinal)
                    .ToArray(),
                SnapshotJsonOptions));

    internal static string NormalizeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteCanonicalJson(writer, document.RootElement);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            return json.Trim();
        }
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private sealed record PermissionExecutionSnapshot(
        int Version,
        Guid RunId,
        long RunRevision,
        string CallId,
        string ArgumentsJson,
        AgentProfileRecord? Profile,
        string? ProviderId,
        string? ModelId,
        AgentWorkspaceRecord? Workspace,
        AgentWorkspaceBindingRecord? Binding,
        AgentExecutionTargetDescriptor? Target,
        string? ExecutionTargetConfigurationGeneration,
        AgentToolDescriptor Descriptor,
        AgentPermissionRequest? PermissionRequest,
        string? ToolOwnerPackageId,
        string? ExecutionTargetOwnerPackageId,
        PermissionDecisionAudit? DecisionAudit);

    private sealed record PermissionDecisionAudit(
        AgentPermissionDecision Decision,
        AgentPermissionDecision BaseDecision,
        AgentPermissionDecisionSource Source,
        string Reason,
        Guid? SourceSessionId,
        DateTimeOffset? OverrideUpdatedAtUtc);
}
