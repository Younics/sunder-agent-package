using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local;

internal static class LocalResourceResolver
{
    internal const string ClaimNamespace = "local-host-resource-claim-v1";

    public static IReadOnlyList<AgentResolvedExecutionResource> ResolveResources(IReadOnlyList<AgentExecutionResourceDescriptor> resources)
        => resources
            .Where(resource => !string.IsNullOrWhiteSpace(resource.HostPath))
            .Select(resource => new AgentResolvedExecutionResource(
                resource.ResourceId,
                resource.ResourceKind,
                resource.SourceId,
                resource.DisplayName,
                resource.HostPath,
                resource.HostPath,
                resource.AccessMode,
                resource.Metadata))
            .ToArray();

    public static AgentResolvedResource ResolveFileResource(
        LocalExecutionRuntimeConfig config,
        string path,
        bool allowOutsideConfiguredScope,
        AgentExecutionTargetContext? context = null,
        LocalResourceReference? resourceReferences = null)
    {
        var fullPath = LocalSecurePathEngine.ResolveLexicalPath(config, path);
        var authority = HostSecurePathEngine.Capture(
            config.WorkspacePaths,
            fullPath);
        return ResolveFileResource(
            config,
            path,
            fullPath,
            allowOutsideConfiguredScope,
            context,
            authority,
            issueOutsideAuthority: context?.ResourceOperation is { CanIssueOutsideAuthority: true },
            context?.ResourceOperation?.AuthorityUseCount ?? 1,
            resourceReferences);
    }

    internal static AgentResolvedResource ResolvePostMutationResource(
        LocalExecutionRuntimeConfig config,
        string path,
        AgentExecutionTargetContext context,
        LocalSecureApprovalLease authority,
        LocalResourceReference resourceReferences)
    {
        try
        {
            var fullPath = LocalSecurePathEngine.ResolveLexicalPath(config, path);
            if (!HostSecurePathEngine.BindingPathEquals(authority.Binding, fullPath))
            {
                throw new LocalSecureApprovalChangedException(fullPath);
            }
            return ResolveFileResource(
                config,
                path,
                fullPath,
                allowOutsideConfiguredScope: true,
                context,
                authority,
                issueOutsideAuthority: true,
                authorityUseCount: 1,
                resourceReferences);
        }
        catch
        {
            authority.Dispose();
            throw;
        }
    }

    private static AgentResolvedResource ResolveFileResource(
        LocalExecutionRuntimeConfig config,
        string path,
        string fullPath,
        bool allowOutsideConfiguredScope,
        AgentExecutionTargetContext? context,
        LocalSecureApprovalLease retainedAuthority,
        bool issueOutsideAuthority,
        int authorityUseCount,
        LocalResourceReference? resourceReferences)
    {
        LocalSecureApprovalLease? authority = retainedAuthority;
        IReadOnlyList<string> authorityReferences = [];
        try
        {
            var binding = authority.Binding;
            var configuredRoot = LocalSecurePathEngine.SelectConfiguredRoot(config, binding.FullPath);
            var boundary = configuredRoot is not null
                ? AgentPermissionBoundaryIds.ConfiguredScope
                : AgentPermissionBoundaryIds.OutsideConfiguredScope;
            if (!allowOutsideConfiguredScope
                && string.Equals(boundary, AgentPermissionBoundaryIds.OutsideConfiguredScope, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Path '{path}' is outside the configured workspace paths.");
            }

            var operation = context?.ResourceOperation;
            var claim = HostResourceClaim.Create(
                ClaimNamespace,
                NormalizeClaimPath(binding.FullPath, binding.CaseSensitivePath),
                configuredRoot is null
                    ? null
                    : NormalizeClaimPath(configuredRoot, binding.CaseSensitivePath),
                authority,
                operation);
            if (context is not null)
            {
                claim = HostResourceClaim.BindScope(claim, context);
            }
            if (configuredRoot is null
                && issueOutsideAuthority
                && operation is not null
                && context is not null)
            {
                var issuanceOperation = operation with
                {
                    AuthorityUseCount = authorityUseCount,
                    CanIssueOutsideAuthority = true,
                };
                authorityReferences = IssueOutsideAuthorityReferences(
                    config,
                    binding.FullPath,
                    claim,
                    issuanceOperation,
                    context,
                    authority,
                    ValidateAuthorityUseCount(authorityUseCount),
                    resourceReferences ?? throw new InvalidOperationException(
                        "Outside Local authority requires an activation-owned capability store."));
                authority = null;
            }
            else if (configuredRoot is null && issueOutsideAuthority)
            {
                throw new InvalidOperationException(
                    "Outside Local authority requires an exact host-owned invocation binding.");
            }
            var resourceReference = HostResourceClaim.CreateReference(claim);
            return new AgentResolvedResource(
                binding.TargetKind == LocalSecureNodeKind.Directory ? "directory" : "file",
                binding.FullPath,
                resourceReference,
                boundary,
                binding.Exists)
            {
                ResourceClaim = claim,
                AuthorityReferences = authorityReferences,
                DeleteCanonicalReference = resourceReference,
                DeleteResourceClaim = claim,
                DeleteAuthorityReferences = authorityReferences,
                DeletePermissionBoundaryId = boundary,
                IsScopedInstructionDocument = string.Equals(
                    Path.GetFileName(binding.FullPath),
                    "AGENTS.md",
                    StringComparison.Ordinal),
            };
        }
        catch
        {
            foreach (var authorityReference in authorityReferences)
            {
                resourceReferences!.Revoke(authorityReference);
            }
            throw;
        }
        finally
        {
            authority?.Dispose();
        }
    }

    internal static string NormalizeClaimPath(string path, bool caseSensitivePath)
    {
        var normalized = HostSecurePathEngine.NormalizePath(path);
        return OperatingSystem.IsWindows() && !caseSensitivePath
            ? normalized.ToUpperInvariant()
            : normalized;
    }

    private static int ValidateAuthorityUseCount(int count)
        => count is > 0 and <= HostResourceClaim.MaximumAuthorityUses
            ? count
            : throw new InvalidOperationException(
                $"A resource operation supports 1 to {HostResourceClaim.MaximumAuthorityUses} authority uses.");

    private static IReadOnlyList<string> IssueOutsideAuthorityReferences(
        LocalExecutionRuntimeConfig config,
        string fullPath,
        AgentResourceClaim claim,
        AgentResourceOperationContext operation,
        AgentExecutionTargetContext context,
        LocalSecureApprovalLease firstAuthority,
        int authorityUseCount,
        LocalResourceReference resourceReferences)
    {
        var authorities = new List<LocalSecureApprovalLease>(authorityUseCount) { firstAuthority };
        var references = new List<string>(authorityUseCount);
        try
        {
            for (var index = 1; index < authorityUseCount; index++)
            {
                var authority = HostSecurePathEngine.Capture(config.WorkspacePaths, fullPath);
                try
                {
                    HostResourceClaim.Validate(
                        claim,
                        ClaimNamespace,
                        claim.LogicalPath,
                        expectedConfiguredRoot: null,
                        authority,
                        context);
                }
                catch
                {
                    authority.Dispose();
                    throw;
                }
                authorities.Add(authority);
            }

            while (authorities.Count > 0)
            {
                var authority = authorities[^1];
                authorities.RemoveAt(authorities.Count - 1);
                try
                {
                    references.Add(resourceReferences.Create(claim, operation, authority));
                }
                catch
                {
                    authority.Dispose();
                    throw;
                }
            }
            return references;
        }
        catch
        {
            foreach (var reference in references)
            {
                resourceReferences.Revoke(reference);
            }
            foreach (var authority in authorities)
            {
                authority.Dispose();
            }
            throw;
        }
    }

    public static AgentExecutionPathMapping MapToHostPath(LocalExecutionRuntimeConfig config, string executionPath)
    {
        var binding = LocalSecurePathEngine.Probe(config, executionPath);
        if (LocalSecurePathEngine.SelectConfiguredRoot(config, binding.FullPath) is null)
        {
            throw new InvalidOperationException($"Path '{executionPath}' is outside the configured workspace paths.");
        }

        // Mapping is advisory. Structured operations must reacquire their own secure handle chain.
        return new AgentExecutionPathMapping(binding.FullPath, binding.FullPath, IsInsideAllowedRoot: true);
    }
}
