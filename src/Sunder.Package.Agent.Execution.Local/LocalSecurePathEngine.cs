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
        var insideConfiguredScope = HostSecurePathEngine.SelectConfiguredRoot(config.WorkspacePaths, fullPath) is not null;
        if (!insideConfiguredScope && !allowOutsideConfiguredScope)
        {
            throw new InvalidOperationException($"Path '{requestedPath}' is outside the configured workspace paths.");
        }
        var capabilities = authorizationContext?.ApprovedResourceCapabilities
                           ?? approvedResourceReferences
                           ?? [];
        if (capabilities.Count > MaxApprovedResourceReferences
            || authorizationContext?.ApprovedResourceClaims.Count > MaxApprovedResourceReferences)
        {
            throw new InvalidOperationException(
                $"Local file operations support at most {MaxApprovedResourceReferences} approved resource leases.");
        }
        if (authorizationContext is null)
        {
            return insideConfiguredScope
                ? null
                : throw new InvalidOperationException(
                    $"Path '{requestedPath}' is outside configured scope without an exact approved Local resource capability.");
        }

        var claim = FindClaim(authorizationContext, fullPath);
        if (claim is null)
        {
            if (insideConfiguredScope && authorizationContext.ApprovedResourceClaims.Count == 0)
            {
                return null;
            }
            throw new LocalResourceReapprovalRequiredException(requestedPath);
        }
        var operation = authorizationContext.ResourceOperation
            ?? throw new LocalResourceReapprovalRequiredException(requestedPath);
        var exactOperation = operation with { ResourceIndex = claim.ResourceIndex };
        var exactContext = authorizationContext with { ResourceOperation = exactOperation };
        LocalSecureApprovalLease? authority = null;
        if (!insideConfiguredScope)
        {
            foreach (var reference in capabilities)
            {
                if (resourceReferences?.TryRedeem(
                        reference,
                        claim,
                        exactOperation,
                        out authority) == true)
                {
                    break;
                }
            }
            if (authority is null)
            {
                throw new LocalResourceReapprovalRequiredException(requestedPath);
            }
        }
        else
        {
            authority = HostSecurePathEngine.Capture(config.WorkspacePaths, fullPath);
        }
        try
        {
            var configuredRoot = HostSecurePathEngine.SelectConfiguredRoot(
                config.WorkspacePaths,
                fullPath);
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
            return authority;
        }
        catch
        {
            authority.Dispose();
            throw;
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
                cancellationToken: cancellationToken);
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

            var configuredRoot = HostSecurePathEngine.SelectConfiguredRoot(
                config.WorkspacePaths,
                claim.LogicalPath);
            if ((configuredRoot is null) != (claim.ConfiguredRoot is null)
                || configuredRoot is not null
                   && !string.Equals(
                       LocalResourceResolver.NormalizeClaimPath(configuredRoot, claim.CaseSensitivePath),
                       claim.ConfiguredRoot,
                       claim.CaseSensitivePath || !OperatingSystem.IsWindows()
                           ? StringComparison.Ordinal
                           : StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (configuredRoot is null
                && !context.ApprovedResourceCapabilities.Any(reference =>
                    resourceReferences.IsCurrent(reference, claim, exactOperation)))
            {
                return false;
            }
        }
        return true;
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
