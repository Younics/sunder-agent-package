using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local;

internal static class LocalSecurePathEngine
{
    private const int MaxApprovedResourceReferences = 128;

    public static string ResolveLexicalPath(LocalExecutionRuntimeConfig config, string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath) || requestedPath.Contains('\0'))
        {
            throw new InvalidOperationException("A Local filesystem path must be non-empty and contain no NUL characters.");
        }
        return LocalPathResolver.ResolvePath(config, requestedPath, allowOutsideConfiguredScope: true);
    }

    public static LocalSecurePathSession OpenAuthorized(
        LocalExecutionRuntimeConfig config,
        string requestedPath,
        bool allowOutsideConfiguredScope,
        IReadOnlyList<string>? approvedResourceReferences,
        bool createParents,
        ILocalSecureFileSystemHooks? hooks,
        CancellationToken cancellationToken,
        LocalResourceReference? resourceReferences = null)
    {
        var fullPath = ResolveLexicalPath(config, requestedPath);
        return HostSecurePathEngine.OpenAuthorized(
            config.WorkspacePaths,
            fullPath,
            ResolveApprovedAuthority(
                config,
                fullPath,
                requestedPath,
                allowOutsideConfiguredScope,
                approvedResourceReferences,
                cancellationToken: cancellationToken,
                resourceReferences: resourceReferences),
            createParents,
            hooks,
            cancellationToken);
    }

    public static LocalResourceBinding Probe(
        LocalExecutionRuntimeConfig config,
        string requestedPath,
        ILocalSecureFileSystemHooks? hooks = null,
        CancellationToken cancellationToken = default)
        => HostSecurePathEngine.Probe(
            config.WorkspacePaths,
            ResolveLexicalPath(config, requestedPath),
            hooks,
            cancellationToken);

    public static LocalSecureRoot OpenRoot(
        string path,
        ILocalSecureFileSystemHooks? hooks = null,
        CancellationToken cancellationToken = default)
        => HostSecurePathEngine.OpenRoot(path, hooks, cancellationToken);

    public static string? SelectConfiguredRoot(LocalExecutionRuntimeConfig config, string fullPath)
        => HostSecurePathEngine.SelectConfiguredRoot(config.WorkspacePaths, fullPath);

    public static bool IsSameOrChild(string candidate, string root)
        => HostSecurePathEngine.IsSameOrChild(candidate, root);

    internal static IReadOnlyList<string> GetRelativeSegments(string root, string target)
        => HostSecurePathEngine.GetRelativeSegments(root, target);

    internal static StringComparer PathComparer => HostSecurePathEngine.PathComparer;

    internal static LocalSecureApprovalLease? ResolveApprovedAuthority(
        LocalExecutionRuntimeConfig config,
        string fullPath,
        string requestedPath,
        bool allowOutsideConfiguredScope,
        IReadOnlyList<string>? approvedResourceReferences,
        AgentExecutionTargetContext? authorizationContext = null,
        CancellationToken cancellationToken = default,
        LocalResourceReference? resourceReferences = null)
    {
        var capabilities = authorizationContext?.ApprovedResourceCapabilities
                           ?? approvedResourceReferences
                           ?? [];
        if (capabilities.Count > MaxApprovedResourceReferences
            || authorizationContext?.ApprovedResourceClaims.Count > MaxApprovedResourceReferences)
        {
            throw new InvalidOperationException(
                $"Local file operations support at most {MaxApprovedResourceReferences} approved resource leases.");
        }
        LocalSecureApprovalLease? authority = null;
        var redeemedOutsideAuthority = false;
        try
        {
            var claim = authorizationContext is null
                ? null
                : FindClaim(authorizationContext, fullPath);
            if (claim is not null && claim.ConfiguredRoot is null)
            {
                var claimedOperation = authorizationContext!.ResourceOperation
                    ?? throw new LocalResourceReapprovalRequiredException(requestedPath);
                var claimedExactOperation = claimedOperation with { ResourceIndex = claim.ResourceIndex };
                foreach (var reference in capabilities)
                {
                    if (resourceReferences?.TryRedeem(
                            reference,
                            claim,
                            claimedExactOperation,
                            out authority) == true)
                    {
                        redeemedOutsideAuthority = true;
                        break;
                    }
                }
            }
            if (authority is null)
            {
                authority = HostSecurePathEngine.Capture(
                    config.WorkspacePaths,
                    fullPath,
                    cancellationToken: cancellationToken,
                    allowMissingSuffix: true);
            }

            var scope = HostSecurePathEngine.ClassifyConfiguredRoot(
                config.WorkspacePaths,
                authority,
                cancellationToken: cancellationToken);
            if (scope.Classification != LocalConfiguredScopeClassification.Configured
                && !authority.Binding.Exists
                && HostSecurePathEngine.GetRelativeSegments(
                    authority.Binding.AnchorPath,
                    authority.Binding.FullPath).Count > 1)
            {
                throw new LocalResourceReapprovalRequiredException(requestedPath);
            }
            var insideConfiguredScope = scope.Classification == LocalConfiguredScopeClassification.Configured;
            if (!insideConfiguredScope && !allowOutsideConfiguredScope)
            {
                throw new InvalidOperationException($"Path '{requestedPath}' is outside the configured workspace paths.");
            }
            if (authorizationContext is null)
            {
                if (!insideConfiguredScope)
                {
                    throw new InvalidOperationException(
                        $"Path '{requestedPath}' is outside configured scope without an exact approved Local resource capability.");
                }
                return HasLexicalConfiguredRoot(config, fullPath)
                    ? null
                    : Detach(ref authority);
            }
            if (claim is null)
            {
                if (insideConfiguredScope && authorizationContext.ApprovedResourceClaims.Count == 0)
                {
                    return HasLexicalConfiguredRoot(config, fullPath)
                        ? null
                        : Detach(ref authority);
                }
                throw new LocalResourceReapprovalRequiredException(requestedPath);
            }
            if (scope.Classification == LocalConfiguredScopeClassification.Unknown
                || !insideConfiguredScope && !redeemedOutsideAuthority)
            {
                throw new LocalResourceReapprovalRequiredException(requestedPath);
            }

            var operation = authorizationContext.ResourceOperation
                ?? throw new LocalResourceReapprovalRequiredException(requestedPath);
            var exactOperation = operation with { ResourceIndex = claim.ResourceIndex };
            var exactContext = authorizationContext with { ResourceOperation = exactOperation };
            var configuredRoot = scope.ConfiguredRoot;
            if (!insideConfiguredScope
                && string.Equals(exactOperation.ActionId, "files.mutate", StringComparison.Ordinal))
            {
                ValidateCurrentMutationBinding(
                    config,
                    fullPath,
                    configuredRoot,
                    claim,
                    exactContext,
                    cancellationToken);
            }
            HostResourceClaim.Validate(
                claim,
                LocalResourceResolver.ClaimNamespace,
                LocalResourceResolver.NormalizeClaimPath(
                    fullPath,
                    claim.CaseSensitivePath),
                configuredRoot is null
                    ? null
                    : LocalResourceResolver.NormalizeClaimPath(
                        configuredRoot,
                        claim.CaseSensitivePath),
                authority,
                exactContext);
            return Detach(ref authority);
        }
        finally
        {
            authority?.Dispose();
        }
    }

    private static void ValidateCurrentMutationBinding(
        LocalExecutionRuntimeConfig config,
        string fullPath,
        string? configuredRoot,
        AgentResourceClaim claim,
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            using var current = HostSecurePathEngine.Capture(
                config.WorkspacePaths,
                fullPath,
                cancellationToken: cancellationToken,
                allowMissingSuffix: false);
            var currentScope = HostSecurePathEngine.ClassifyConfiguredRoot(
                config.WorkspacePaths,
                current,
                cancellationToken: cancellationToken);
            if (currentScope.Classification == LocalConfiguredScopeClassification.Unknown
                || !ConfiguredRootsEqual(
                    configuredRoot,
                    currentScope.ConfiguredRoot,
                    claim.CaseSensitivePath))
            {
                throw new LocalSecureApprovalChangedException(fullPath);
            }
            HostResourceClaim.Validate(
                claim,
                LocalResourceResolver.ClaimNamespace,
                LocalResourceResolver.NormalizeClaimPath(fullPath, claim.CaseSensitivePath),
                configuredRoot is null
                    ? null
                    : LocalResourceResolver.NormalizeClaimPath(configuredRoot, claim.CaseSensitivePath),
                current,
                context);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not LocalSecureApprovalChangedException)
        {
            throw new LocalSecureApprovalChangedException(fullPath);
        }
    }

    internal static bool HasCurrentOutsideCapabilities(
        LocalExecutionRuntimeConfig config,
        AgentExecutionTargetContext context,
        LocalResourceReference resourceReferences)
    {
        if (context.ApprovedResourceClaims.Count == 0)
        {
            return !context.AllowOutsideConfiguredScope;
        }
        if (context.ApprovedResourceClaims.Count > MaxApprovedResourceReferences
            || context.ApprovedResourceCapabilities.Count > MaxApprovedResourceReferences
            || context.ResourceOperation is not { } operation)
        {
            return false;
        }

        foreach (var claim in context.ApprovedResourceClaims)
        {
            var exactOperation = operation with { ResourceIndex = claim.ResourceIndex };
            if (!string.Equals(claim.NamespaceId, LocalResourceResolver.ClaimNamespace, StringComparison.Ordinal)
                || !HostResourceClaim.MatchesOperation(claim, exactOperation))
            {
                return false;
            }

            LocalConfiguredRootResolution scope;
            try
            {
                using var authority = HostSecurePathEngine.Capture(
                    config.WorkspacePaths,
                    claim.LogicalPath,
                    allowMissingSuffix: claim.ConfiguredRoot is not null);
                scope = HostSecurePathEngine.ClassifyConfiguredRoot(
                    config.WorkspacePaths,
                    authority);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return false;
            }
            if (scope.Classification == LocalConfiguredScopeClassification.Unknown
                || !ConfiguredRootsEqual(
                    scope.ConfiguredRoot,
                    claim.ConfiguredRoot,
                    claim.CaseSensitivePath))
            {
                return false;
            }

            if (scope.ConfiguredRoot is null
                && !context.ApprovedResourceCapabilities.Any(reference =>
                    resourceReferences.IsCurrent(reference, claim, exactOperation)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ConfiguredRootsEqual(
        string? left,
        string? right,
        bool caseSensitivePath)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }
        return string.Equals(
            LocalResourceResolver.NormalizeClaimPath(left, caseSensitivePath),
            LocalResourceResolver.NormalizeClaimPath(right, caseSensitivePath),
            caseSensitivePath || !OperatingSystem.IsWindows()
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasLexicalConfiguredRoot(
        LocalExecutionRuntimeConfig config,
        string fullPath)
        => HostSecurePathEngine.SelectConfiguredRoot(config.WorkspacePaths, fullPath) is not null;

    private static LocalSecureApprovalLease Detach(ref LocalSecureApprovalLease? authority)
    {
        var detached = authority
            ?? throw new InvalidOperationException("Local resource authority was not captured.");
        authority = null;
        return detached;
    }

    private static AgentResourceClaim? FindClaim(
        AgentExecutionTargetContext context,
        string fullPath)
    {
        var operation = context.ResourceOperation;
        var matches = context.ApprovedResourceClaims
            .Where(claim => string.Equals(
                                claim.NamespaceId,
                                LocalResourceResolver.ClaimNamespace,
                                StringComparison.Ordinal)
                            && string.Equals(
                                claim.LogicalPath,
                                LocalResourceResolver.NormalizeClaimPath(fullPath, claim.CaseSensitivePath),
                                claim.CaseSensitivePath || !OperatingSystem.IsWindows()
                                    ? StringComparison.Ordinal
                                    : StringComparison.OrdinalIgnoreCase)
                            && (operation is null
                                || string.Equals(claim.ActionId, operation.ActionId, StringComparison.Ordinal)
                                && string.Equals(claim.ToolCallId, operation.ToolCallId, StringComparison.Ordinal)))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}

internal sealed class LocalResourceReapprovalRequiredException(string path)
    : InvalidOperationException($"Outside Local resource authority for '{path}' expired or is unavailable; explicit reapproval is required.");
