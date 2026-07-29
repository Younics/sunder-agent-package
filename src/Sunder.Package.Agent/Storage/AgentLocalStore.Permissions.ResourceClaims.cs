using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private static readonly JsonSerializerOptions PermissionJsonOptions = new(JsonSerializerDefaults.Web);

    private static AgentPendingPermissionRequestRecord NormalizeResourceClaims(
        AgentPendingPermissionRequestRecord record)
    {
        if (record.ResourceReference?.StartsWith("local-resource-v3:", StringComparison.Ordinal) == true
            || record.ResourceReference?.StartsWith("docker-resource-v3:", StringComparison.Ordinal) == true
            || record.ResourceReference?.Contains("resource-authority-v4:", StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException(
                "Transient or legacy resource authority cannot be persisted in a permission request.");
        }

        var claims = record.ResourceClaims
            .Where(static claim => claim is not null)
            .OrderBy(static claim => claim.ResourceIndex)
            .ToArray();
        if (claims.Length > 128
            || claims.Any(claim => claim.Version != 1
                                   || claim.ResourceIndex < 0
                                   || string.IsNullOrWhiteSpace(claim.NamespaceId)
                                   || string.IsNullOrWhiteSpace(claim.LogicalPath)
                                   || string.IsNullOrWhiteSpace(claim.RootIdentityChainFingerprint)
                                   || string.IsNullOrWhiteSpace(claim.ActionId)
                                   || string.IsNullOrWhiteSpace(claim.WorkspaceId)
                                   || string.IsNullOrWhiteSpace(claim.WorkspaceGeneration)
                                   || string.IsNullOrWhiteSpace(claim.BindingId)
                                   || string.IsNullOrWhiteSpace(claim.BindingGeneration)
                                   || string.IsNullOrWhiteSpace(claim.ToolCallId)
                                   || string.IsNullOrWhiteSpace(claim.ToolOwnerPackageId)
                                   || string.IsNullOrWhiteSpace(claim.ExecutionTargetOwnerPackageId)))
        {
            throw new InvalidOperationException("The permission request contains an invalid durable resource claim set.");
        }
        var serialized = SerializeResourceClaims(claims);
        if (serialized.Length > 262_144)
        {
            throw new InvalidOperationException("The durable resource claim set exceeds its persistence bound.");
        }
        return record with
        {
            ResourceClaimSetVersion = claims.Length == 0 ? 0 : 1,
            ResourceClaims = claims,
        };
    }

    private static string SerializeResourceClaims(IReadOnlyList<AgentResourceClaim> claims)
        => JsonSerializer.Serialize(claims, PermissionJsonOptions);

    private static IReadOnlyList<AgentResourceClaim> DeserializeResourceClaims(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentResourceClaim[]>(json, PermissionJsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("A persisted durable resource claim set is malformed.", ex);
        }
    }
}
