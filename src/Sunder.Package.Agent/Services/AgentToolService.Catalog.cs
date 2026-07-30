using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

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
        CancellationToken cancellationToken)
    {
        var candidates = new List<OwnedRuntimeToolCandidate>();
        var installedSource = new AgentToolSourceMetadata(
            _installedPackageToolSource.SourceId,
            _installedPackageToolSource.DisplayName,
            _installedPackageToolSource.SourceKind,
            SupportsPermission: false,
            SupportsPreflight: false);
        foreach (var reference in _invocationCatalog.GetExtensionReferences(PackageExtensionPoints.Tools))
        {
            if (!reference.TryAcquire(out var lease))
            {
                continue;
            }

            using (lease)
            {
                var descriptor = WithSourceIdentity(installedSource, lease.Contribution.Descriptor);
                if (lease.RetirementToken.IsCancellationRequested)
                {
                    continue;
                }

                var targetSnapshot = SnapshotExecutionTarget(context.ExecutionTargetReference);
                var invocation = new AgentToolInvocationReference(
                    lease.PackageId,
                    descriptor,
                    reference,
                    SourceReference: null,
                    SupportsInstalledPermission: lease.Contribution is IAgentPermissionAwareTool,
                    SupportsSourcePermission: false,
                    SupportsPreflight: false,
                    context.ExecutionTargetReference,
                    targetSnapshot?.Descriptor,
                    targetSnapshot?.OwnerPackageId,
                    Guid.NewGuid().ToString("N"));
                candidates.Add(new OwnedRuntimeToolCandidate(
                    CreateRuntimeTool(descriptor),
                    invocation));
            }
        }

        var sourceReferences = AgentExtensionInvocation.Snapshot(
            _invocationCatalog,
            PackageExtensionPoints.ToolSources,
            static source => new AgentToolSourceMetadata(
                source.SourceId,
                source.DisplayName,
                source.SourceKind,
                source is IAgentPermissionAwareToolSource,
                source is IAgentToolExecutionPreflightSource));
        foreach (var sourceReference in sourceReferences
                     .OrderBy(source => source.Metadata.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<AgentRuntimeTool> runtimeTools;
            try
            {
                runtimeTools = await InvokeTargetBoundAsync(
                    context.ExecutionTargetReference,
                    cancellationToken,
                    token => AgentExtensionInvocation.InvokeAsync(
                        sourceReference,
                        token,
                        (source, invocationToken) => new ValueTask<IReadOnlyList<AgentRuntimeTool>>(
                            ListRuntimeToolsAsync(source, context, invocationToken)))).ConfigureAwait(false);
            }
            catch (AgentPackageUnavailableException)
            {
                continue;
            }

            foreach (var runtimeTool in runtimeTools)
            {
                var descriptor = WithSourceIdentity(sourceReference.Metadata, runtimeTool.Descriptor);
                var targetSnapshot = SnapshotExecutionTarget(context.ExecutionTargetReference);
                var invocation = new AgentToolInvocationReference(
                    sourceReference.PackageId,
                    descriptor,
                    ToolReference: null,
                    sourceReference.Reference,
                    SupportsInstalledPermission: false,
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
            static async (installedTool, token) =>
                await installedTool.GetReadinessAsync(token).ConfigureAwait(false),
            (source, token) => source.GetReadinessAsync(
                candidate.RuntimeTool.Descriptor.ToolId,
                context,
                token));
}
