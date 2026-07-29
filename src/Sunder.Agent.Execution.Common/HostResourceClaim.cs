using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Agent.Execution.Common;

internal static class HostResourceClaim
{
    public const int CurrentVersion = 1;
    public const int MaximumAuthorityUses = 8;

    public static AgentResourceClaim Create(
        string namespaceId,
        string logicalPath,
        string? configuredRoot,
        LocalSecureApprovalLease authority,
        AgentResourceOperationContext? operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalPath);
        ArgumentNullException.ThrowIfNull(authority);
        return new AgentResourceClaim(
            CurrentVersion,
            namespaceId,
            logicalPath,
            configuredRoot,
            FingerprintIdentityChain(authority.RetainedPathIdentities),
            authority.Binding.Exists,
            authority.Binding.TargetKind?.ToString(),
            authority.Binding.TargetIdentity?.ToString(),
            authority.Binding.TargetIsAuthorityRoot,
            authority.Binding.CaseSensitivePath,
            operation?.ActionId ?? string.Empty,
            string.Empty,
            operation?.WorkspaceGeneration ?? string.Empty,
            string.Empty,
            operation?.BindingGeneration ?? string.Empty,
            operation?.ToolCallId ?? string.Empty,
            operation?.ResourceIndex ?? 0,
            operation?.ToolOwnerPackageId ?? string.Empty,
            operation?.ExecutionTargetOwnerPackageId ?? string.Empty);
    }

    public static AgentResourceClaim BindScope(
        AgentResourceClaim claim,
        AgentExecutionTargetContext context)
        => claim with
        {
            WorkspaceId = context.Workspace.WorkspaceId,
            BindingId = context.Binding.BindingId,
        };

    public static string CreateReference(AgentResourceClaim claim)
        => string.Concat(claim.NamespaceId, ":", FingerprintClaim(claim));

    public static string FingerprintClaim(AgentResourceClaim claim)
    {
        var material = new StringBuilder();
        Append(material, claim.Version.ToString(CultureInfo.InvariantCulture));
        Append(material, claim.NamespaceId);
        Append(material, claim.LogicalPath);
        Append(material, claim.ConfiguredRoot ?? string.Empty);
        Append(material, claim.RootIdentityChainFingerprint);
        Append(material, claim.TargetExists ? "1" : "0");
        Append(material, claim.TargetKind ?? string.Empty);
        Append(material, claim.TargetIdentity ?? string.Empty);
        Append(material, claim.TargetIsAuthorityRoot ? "1" : "0");
        Append(material, claim.CaseSensitivePath ? "1" : "0");
        Append(material, claim.ActionId);
        Append(material, claim.WorkspaceId);
        Append(material, claim.WorkspaceGeneration);
        Append(material, claim.BindingId);
        Append(material, claim.BindingGeneration);
        Append(material, claim.ToolCallId);
        Append(material, claim.ResourceIndex.ToString(CultureInfo.InvariantCulture));
        Append(material, claim.ToolOwnerPackageId);
        Append(material, claim.ExecutionTargetOwnerPackageId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())))
            .ToLowerInvariant();
    }

    public static string FingerprintIdentityChain(IEnumerable<LocalSecureIdentity> identities)
    {
        var material = new StringBuilder();
        foreach (var identity in identities)
        {
            Append(material, identity.ToString());
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())))
            .ToLowerInvariant();
    }

    public static void Validate(
        AgentResourceClaim claim,
        string expectedNamespaceId,
        string expectedLogicalPath,
        string? expectedConfiguredRoot,
        LocalSecureApprovalLease authority,
        AgentExecutionTargetContext context)
    {
        var operation = context.ResourceOperation;
        var pathComparison = claim.CaseSensitivePath || !OperatingSystem.IsWindows()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        if (claim.Version != CurrentVersion
            || !string.Equals(claim.NamespaceId, expectedNamespaceId, StringComparison.Ordinal)
            || !string.Equals(claim.LogicalPath, expectedLogicalPath, pathComparison)
            || !string.Equals(claim.ConfiguredRoot, expectedConfiguredRoot, pathComparison)
            || !string.Equals(
                claim.RootIdentityChainFingerprint,
                FingerprintIdentityChain(authority.RetainedPathIdentities),
                StringComparison.Ordinal)
            || claim.TargetExists != authority.Binding.Exists
            || !string.Equals(claim.TargetKind, authority.Binding.TargetKind?.ToString(), StringComparison.Ordinal)
            || !string.Equals(claim.TargetIdentity, authority.Binding.TargetIdentity?.ToString(), StringComparison.Ordinal)
            || claim.TargetIsAuthorityRoot != authority.Binding.TargetIsAuthorityRoot
            || claim.CaseSensitivePath != authority.Binding.CaseSensitivePath
            || !string.Equals(claim.WorkspaceId, context.Workspace.WorkspaceId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(claim.BindingId, context.Binding.BindingId, StringComparison.OrdinalIgnoreCase)
            || operation is null
            || !MatchesOperation(claim, operation))
        {
            throw new LocalSecureApprovalChangedException(expectedLogicalPath);
        }
    }

    public static bool MatchesOperation(
        AgentResourceClaim claim,
        AgentResourceOperationContext operation)
        => string.Equals(claim.ActionId, operation.ActionId, StringComparison.Ordinal)
           && claim.ResourceIndex == operation.ResourceIndex
           && string.Equals(claim.WorkspaceGeneration, operation.WorkspaceGeneration, StringComparison.Ordinal)
           && string.Equals(claim.BindingGeneration, operation.BindingGeneration, StringComparison.Ordinal)
           && string.Equals(claim.ToolCallId, operation.ToolCallId, StringComparison.Ordinal)
           && string.Equals(claim.ToolOwnerPackageId, operation.ToolOwnerPackageId, StringComparison.Ordinal)
           && string.Equals(
               claim.ExecutionTargetOwnerPackageId,
               operation.ExecutionTargetOwnerPackageId,
               StringComparison.Ordinal);

    private static void Append(StringBuilder builder, string value)
        => builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
}
