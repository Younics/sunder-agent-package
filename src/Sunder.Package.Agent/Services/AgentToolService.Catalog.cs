using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentToolService
{
    public async Task<IReadOnlyList<AgentToolCatalogEntry>> ListToolCatalogAsync(
        AgentProfileRecord? profile = null,
        Guid? sessionId = null,
        AgentWorkspaceRecord? workspace = null,
        bool includeUnavailable = true,
        CancellationToken cancellationToken = default)
    {
        var effectiveProfile = profile;
        var (context, executionTarget) = await CreateSourceContextAsync(
                sessionId,
                effectiveProfile,
                workspace,
                cancellationToken)
            .ConfigureAwait(false);
        var catalog = new List<AgentToolCatalogEntry>();
        foreach (var candidate in await ListOwnedRuntimeToolCandidatesAsync(
                         context,
                         executionTarget,
                         cancellationToken,
                         omitUnavailableSources: true)
                     .ConfigureAwait(false))
        {
            var descriptor = candidate.RuntimeTool.Descriptor;
            AgentToolReadiness? readiness;
            try
            {
                readiness = await GetReadinessAsync(candidate, context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            {
                continue;
            }
            readiness ??= new AgentToolReadiness(
                descriptor.ToolId,
                AgentToolReadinessStatus.Ready,
                "Ready.");

            if (!includeUnavailable && readiness.Status != AgentToolReadinessStatus.Ready)
            {
                continue;
            }

            catalog.Add(new AgentToolCatalogEntry(descriptor, readiness));
        }

        return catalog
            .OrderByDescending(item => item.Descriptor.Priority)
            .ThenBy(item => item.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<AgentToolCatalogEntry>> ListInstalledLocalToolsAsync(CancellationToken cancellationToken = default)
    {
        var (context, executionTarget) = await CreateSourceContextAsync(
                sessionId: null,
                profile: null,
                workspace: null,
                cancellationToken)
            .ConfigureAwait(false);
        var catalog = new List<AgentToolCatalogEntry>();
        foreach (var candidate in await ListOwnedRuntimeToolCandidatesAsync(
                         context,
                         executionTarget,
                         cancellationToken,
                         omitUnavailableSources: true)
                     .ConfigureAwait(false))
        {
            catalog.AddRange(new[] { candidate.RuntimeTool.Descriptor }
                .Where(descriptor => descriptor.SelectionScope == AgentToolSelectionScope.Tool)
                .Select(descriptor => new AgentToolCatalogEntry(
                descriptor,
                new AgentToolReadiness(
                    descriptor.ToolId,
                    AgentToolReadinessStatus.Ready,
                    "Installed capability. Runtime readiness depends on the selected session workspace."))));
        }

        return catalog
            .GroupBy(item => item.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.Descriptor.Priority)
            .ThenBy(item => item.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<AgentToolReadiness>> ListToolReadinessAsync(CancellationToken cancellationToken = default)
        => (await ListToolCatalogAsync(includeUnavailable: true, cancellationToken: cancellationToken))
            .Select(item => item.Readiness)
            .ToArray();

    public async Task<IReadOnlyList<AgentToolDescriptor>> ListReadyToolDescriptorsAsync(
        AgentProfileRecord? profile = null,
        Guid? sessionId = null,
        AgentWorkspaceRecord? workspace = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveProfile = profile;
        return (await ListToolCatalogAsync(profile, sessionId, workspace, includeUnavailable: false, cancellationToken))
            .Where(item => IsAllowedForProfile(effectiveProfile, item.Descriptor))
            .Select(item => item.Descriptor)
            .ToArray();
    }

    public async Task<IReadOnlyList<AgentRuntimeTool>> ListReadyRuntimeToolsAsync(
        AgentProfileRecord? profile = null,
        Guid? sessionId = null,
        AgentWorkspaceRecord? workspace = null,
        CancellationToken cancellationToken = default)
        => (await ListReadyOwnedRuntimeToolsAsync(
                profile,
                sessionId,
                workspace,
                cancellationToken)
            .ConfigureAwait(false))
            .Select(static tool => tool.RuntimeTool)
            .ToArray();

    internal async Task<IReadOnlyList<AgentOwnedRuntimeTool>> ListReadyOwnedRuntimeToolsAsync(
        AgentProfileRecord? profile = null,
        Guid? sessionId = null,
        AgentWorkspaceRecord? workspace = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveProfile = profile;
        var (context, executionTarget) = await CreateSourceContextAsync(
                sessionId,
                effectiveProfile,
                workspace,
                cancellationToken)
            .ConfigureAwait(false);
        var candidates = await ListOwnedRuntimeToolCandidatesAsync(
                context, executionTarget, cancellationToken, mapUnavailableSources: true)
            .ConfigureAwait(false);
        var tools = new List<AgentOwnedRuntimeTool>();
        foreach (var candidate in candidates
                     .GroupBy(static item => item.RuntimeTool.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() == 1)
                     .Select(static group => group.Single()))
        {
            var descriptor = candidate.RuntimeTool.Descriptor;
            AgentToolReadiness? readiness;
            try
            {
                readiness = await GetReadinessAsync(candidate, context, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            {
                throw new AgentPackageUnavailableException(candidate.Invocation.OwnerPackageId, exception);
            }
            readiness ??= new AgentToolReadiness(
                descriptor.ToolId,
                AgentToolReadinessStatus.Ready,
                "Ready.");
            if (readiness.Status != AgentToolReadinessStatus.Ready
                || !IsAllowedForProfile(effectiveProfile, descriptor))
            {
                continue;
            }

            tools.Add(new AgentOwnedRuntimeTool(
                candidate.RuntimeTool,
                candidate.Invocation.OwnerPackageId,
                candidate.Invocation));
        }

        return tools
            .OrderByDescending(item => item.RuntimeTool.Descriptor.Priority)
            .ThenBy(item => item.RuntimeTool.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RuntimeTool.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal async ValueTask<AgentToolPresentation?> ResolvePresentationAsync(
        AgentToolPresentationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var catalog = await _sources.ListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var provider in catalog.Providers)
            {
                if (provider.State != ServiceProviderState.Active) continue;
                try
                {
                    var presentation = await provider.Service.PresentationAsync(request, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (presentation is not null) return presentation;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (!PresentationExceptionPolicy.IsFatal(exception))
                {
                    // One optional presentation must not hide another source's result.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (!PresentationExceptionPolicy.IsFatal(exception))
        {
            // App retains its local transcript presentation when the catalog is unavailable.
        }
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    private async Task<ResolvedTool?> ResolveAdvertisedToolAsync(
        string toolId,
        AgentToolExecutionContext context,
        AgentToolDescriptor? advertisedDescriptor,
        string? advertisedOwnerPackageId,
        AgentToolInvocationReference? advertisedInvocation,
        ServiceProvider<AgentServices.IExecutionTargetService>? executionTarget,
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
            ExecutionTarget = context.ExecutionTarget,
            ExecutionTargetConfigurationGeneration = context.ExecutionTargetConfigurationGeneration,
        };
        var matching = advertisedInvocation is null
            ? (await ListOwnedRuntimeToolCandidatesAsync(sourceContext, executionTarget, cancellationToken)
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
             // Exact prepared invocations were selected before approval. Profile edits
             // must not revoke them; new/unprepared calls still require assignment.
             || advertisedInvocation is null && !IsAllowedForProfile(profile, descriptor))
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
        ServiceProvider<AgentServices.IExecutionTargetService>? executionTarget,
        CancellationToken cancellationToken,
        bool omitUnavailableSources = false,
        bool mapUnavailableSources = false)
    {
        var candidates = new List<OwnedRuntimeToolCandidate>();
        var descriptorContext = context with
        {
            ExecutionTarget = null,
            ExecutionTargetConfigurationGeneration = null,
        };
        var sourceCatalog = await _sources.ListAsync(cancellationToken).ConfigureAwait(false);
        var sources = new List<(ServiceProvider<AgentServices.IToolSourceService> Provider, AgentToolSourceDescriptor Descriptor)>();
        foreach (var source in sourceCatalog.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.State != ServiceProviderState.Active) continue;
            try
            {
                sources.Add((source, await source.Service.DescribeAsync(cancellationToken).ConfigureAwait(false)));
            }
            catch (SunderRpcException exception) when ((omitUnavailableSources || mapUnavailableSources) && !cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            {
                if (mapUnavailableSources)
                {
                    throw new AgentPackageUnavailableException(source.PackageId, exception);
                }
                // Optional catalog presentation omits withdrawn tool sources.
            }
        }
        var observedCatalog = await _observedExecutions.ListAsync(cancellationToken).ConfigureAwait(false);
        var observedExecutions = new List<(ServiceProvider<AgentServices.IToolExecutionObservedService> Provider, string SourceId)>();
        foreach (var execution in observedCatalog.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (execution.State != ServiceProviderState.Active) continue;
            try
            {
                observedExecutions.Add((execution, await execution.Service.DescribeAsync(cancellationToken).ConfigureAwait(false)));
            }
            catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            {
                // Observation is optional; exact owner/source matching below decides its use.
            }
        }
        var targetSnapshot = await SnapshotExecutionTargetAsync(executionTarget, cancellationToken).ConfigureAwait(false);
        foreach (var (provider, source) in sources
                     .OrderBy(source => source.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<AgentToolDescriptor> tools;
            try
            {
                tools = await provider.Service.ListToolsAsync(descriptorContext, cancellationToken).ConfigureAwait(false);
            }
            catch (SunderRpcException exception) when ((omitUnavailableSources || mapUnavailableSources) && !cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            {
                if (mapUnavailableSources)
                {
                    throw new AgentPackageUnavailableException(provider.PackageId, exception);
                }
                continue;
            }

            foreach (var tool in tools)
            {
                var descriptor = WithSourceIdentity(source, tool);
                var observed = observedExecutions
                    .Where(reference => string.Equals(
                                            reference.Provider.PackageId,
                                            provider.PackageId,
                                            StringComparison.Ordinal)
                                        && string.Equals(
                                            reference.SourceId,
                                            source.SourceId,
                                            StringComparison.Ordinal))
                    .Select(reference => reference.Provider)
                    .Take(2)
                    .ToArray();
                var invocation = new AgentToolInvocationReference(
                    provider.PackageId,
                    descriptor,
                    provider,
                    observed.Length == 1
                        ? observed[0]
                        : null,
                    executionTarget,
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
        => candidate.Invocation.SourceProvider.Service.GetReadinessAsync(
                candidate.RuntimeTool.Descriptor.ToolId,
                context,
                cancellationToken);
}
