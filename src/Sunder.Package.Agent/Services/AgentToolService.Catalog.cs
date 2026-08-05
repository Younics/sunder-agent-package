using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentToolService
{
    private async Task<ResolvedTool?> ResolveAdvertisedToolAsync(
        string toolId,
        AgentToolExecutionContext context,
        AgentToolDescriptor? advertisedDescriptor,
        string? advertisedOwnerPackageId,
        AgentToolInvocationReference? advertisedInvocation,
        CancellationToken cancellationToken,
        bool requireReadiness = true)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return null;
        }

        var profile = ResolveProfile(context.ProfileId);
        if (!string.IsNullOrWhiteSpace(context.ProfileId) && profile is null)
        {
            return null;
        }

        var sourceContext = new AgentToolSourceContext(context.SessionId, profile, context.Workspace, context.ExecutionBinding)
        {
            ExecutionTargetReference = context.ExecutionTargetReference,
            ExecutionTargetConfigurationGeneration = context.ExecutionTargetConfigurationGeneration,
        };
        var matching = advertisedInvocation is null
            ? (await ListOwnedRuntimeToolCandidatesAsync(sourceContext, cancellationToken)
                    .ConfigureAwait(false))
                .Where(candidate => string.Equals(
                    candidate.RuntimeTool.Descriptor.ToolId,
                    toolId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : string.Equals(
                advertisedInvocation.Descriptor.ToolId,
                toolId,
                StringComparison.OrdinalIgnoreCase)
                ? [new OwnedRuntimeToolCandidate(
                    CreateRuntimeTool(advertisedInvocation.Descriptor),
                    advertisedInvocation)]
                : [];
        if (matching.Length != 1)
        {
            return null;
        }

        var candidate = matching[0];
        var descriptor = candidate.RuntimeTool.Descriptor;
        if (advertisedDescriptor is not null && !IsSameAdvertisedTool(advertisedDescriptor, descriptor)
            || advertisedOwnerPackageId is not null
               && !string.Equals(
                   advertisedOwnerPackageId,
                    candidate.Invocation.OwnerPackageId,
                   StringComparison.Ordinal)
            || !IsAllowedForProfile(profile, descriptor))
        {
            return null;
        }

        if (requireReadiness)
        {
            var readiness = await GetReadinessAsync(candidate, sourceContext, cancellationToken)
                .ConfigureAwait(false);
            if (readiness is not null && readiness.Status != AgentToolReadinessStatus.Ready)
            {
                return null;
            }
        }

        return new ResolvedTool(descriptor, candidate.Invocation);
    }

    private async Task<IReadOnlyList<OwnedRuntimeToolCandidate>> ListOwnedRuntimeToolCandidatesAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken,
        bool omitUnavailableSources = false)
    {
        var candidates = new List<OwnedRuntimeToolCandidate>();
        var descriptorContext = context with
        {
            ExecutionTargetReference = null,
            ExecutionTargetConfigurationGeneration = null,
        };
        var sourceReferences = await AgentRpcInvocation.SnapshotAsync(
                _rpcCatalog,
                AgentRpcServices.ToolSources,
                static (source, token) => DescribeToolSourceAsync(source, token),
                cancellationToken,
                omitUnavailableSources)
            .ConfigureAwait(false);
        foreach (var sourceReference in sourceReferences
                     .OrderBy(source => source.Metadata.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<AgentRuntimeTool> runtimeTools;
            try
            {
                runtimeTools = await AgentRpcInvocation.InvokeAsync(
                    sourceReference,
                    cancellationToken,
                    (source, invocationToken) => new ValueTask<IReadOnlyList<AgentRuntimeTool>>(
                        ListRuntimeToolsAsync(source, descriptorContext, invocationToken))).ConfigureAwait(false);
            }
            catch (AgentPackageUnavailableException) when (omitUnavailableSources)
            {
                continue;
            }

            var targetSnapshot = await SnapshotExecutionTargetAsync(
                    context.ExecutionTargetReference,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var runtimeTool in runtimeTools)
            {
                var descriptor = WithSourceIdentity(sourceReference.Metadata, runtimeTool.Descriptor);
                var invocation = new AgentToolInvocationReference(
                    sourceReference.PackageId,
                    descriptor,
                    sourceReference.Reference,
                    sourceReference.Metadata.SupportsPermission,
                    sourceReference.Metadata.SupportsPreflight,
                    context.ExecutionTargetReference,
                    targetSnapshot?.Descriptor,
                    targetSnapshot?.OwnerPackageId,
                    Guid.NewGuid().ToString("N"));
                candidates.Add(new OwnedRuntimeToolCandidate(
                    CreateRuntimeTool(descriptor),
                    invocation));
            }
        }
        return candidates;
    }

    private static ValueTask<AgentToolReadiness?> GetReadinessAsync(
        OwnedRuntimeToolCandidate candidate,
        AgentToolSourceContext context,
        CancellationToken cancellationToken)
        => InvokeToolAsync<AgentToolReadiness?>(
            candidate.Invocation,
            cancellationToken,
            (source, token) => source.GetReadinessAsync(
                candidate.RuntimeTool.Descriptor.ToolId,
                context,
                token));

    private static async ValueTask<AgentToolSourceMetadata> DescribeToolSourceAsync(
        IAgentToolSource source,
        CancellationToken cancellationToken)
    {
        var description = source is AgentToolSourceRpcClient rpcSource
            ? await rpcSource.DescribeAsync(cancellationToken).ConfigureAwait(false)
            : new ToolSourceDescription(source.SourceId, source.DisplayName, source.SourceKind);
        return new AgentToolSourceMetadata(
            description.SourceId,
            description.DisplayName,
            description.SourceKind,
            SupportsPermission: true,
            SupportsPreflight: true);
    }
}
