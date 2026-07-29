using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Files;

internal sealed partial class ScopedInstructionContextService
{
    private static async ValueTask<AgentScopedInstructionDiscoveryResult> DiscoverBatchedAsync(
        IAgentScopedInstructionDiscoveryTarget target,
        AgentExecutionTargetContext context,
        IReadOnlyList<AgentScopedInstructionProbe> probes,
        CancellationToken cancellationToken)
    {
        if (probes.Count == 0)
        {
            throw new InvalidOperationException("Scoped instruction discovery requires at least one probe.");
        }

        string? targetFingerprint = null;
        string? scopeFingerprint = null;
        var scopes = new List<AgentScopedInstructionScope>();
        foreach (var batch in probes.Chunk(MaxMutationPaths))
        {
            var result = await target.DiscoverScopedInstructionsAsync(
                context,
                new AgentScopedInstructionDiscoveryRequest(batch),
                cancellationToken).ConfigureAwait(false);
            ValidateCompleteDiscovery(result, batch.Length);
            targetFingerprint ??= result.TargetFingerprint;
            scopeFingerprint ??= result.ScopeFingerprint;
            if (!string.Equals(targetFingerprint, result.TargetFingerprint, StringComparison.Ordinal)
                || !string.Equals(scopeFingerprint, result.ScopeFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The execution target changed during scoped instruction discovery.");
            }
            scopes.AddRange(result.Scopes);
        }

        var mergedScopes = scopes
            .GroupBy(scope => scope.TargetDirectory, StringComparer.Ordinal)
            .Select(group =>
            {
                var selected = group.First();
                if (group.Any(scope => !string.Equals(scope.ScopeRoot, selected.ScopeRoot, StringComparison.Ordinal)
                                       || !HaveSameDocuments(scope.Documents, selected.Documents)))
                {
                    throw new InvalidOperationException("Scoped instruction aliases resolved to inconsistent canonical scope data.");
                }
                return selected;
            })
            .ToArray();
        return new AgentScopedInstructionDiscoveryResult(
            targetFingerprint!,
            scopeFingerprint!,
            mergedScopes,
            WasTruncated: false)
        {
            ProcessedProbeCount = probes.Count,
        };
    }

    private static void ValidateCompleteDiscovery(
        AgentScopedInstructionDiscoveryResult result,
        int expectedProbeCount)
    {
        if (result.ProcessedProbeCount != expectedProbeCount
            || result.WasTruncated
            || result.Scopes.Any(scope => scope.WasTruncated || scope.OmittedAncestorCount != 0)
            || result.Scopes.SelectMany(scope => scope.Documents).Any(document => !IsValidDocument(document)))
        {
            throw new InvalidOperationException("Scoped instruction discovery returned partial, duplicate, or invalid scope data.");
        }
        if (result.TargetFingerprint is not { Length: 64 }
            || result.ScopeFingerprint is not { Length: 64 })
        {
            throw new InvalidOperationException("Scoped instruction discovery returned invalid target identity data.");
        }

        foreach (var scope in result.Scopes)
        {
            if (scope.Documents.Select(document => document.Path).Distinct(StringComparer.Ordinal).Count() != scope.Documents.Count)
            {
                throw new InvalidOperationException("Scoped instruction discovery returned duplicate document paths.");
            }
        }
    }

    private static bool HaveSameDocuments(
        IReadOnlyList<AgentScopedInstructionDocument> left,
        IReadOnlyList<AgentScopedInstructionDocument> right)
        => left.Count == right.Count
           && left.OrderBy(document => document.Path, StringComparer.Ordinal)
               .Zip(right.OrderBy(document => document.Path, StringComparer.Ordinal))
               .All(pair => string.Equals(pair.First.Path, pair.Second.Path, StringComparison.Ordinal)
                            && string.Equals(pair.First.ContentHash, pair.Second.ContentHash, StringComparison.Ordinal));
}
