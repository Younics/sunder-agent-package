using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Tests;

// Local execution fakes retain their filesystem/process controls. The boundary is
// the natural service signature, including transient configuration and authority.
internal sealed class RegressionTestExecutionProvider(IAgentExecutionTarget target) : AgentServices.IExecutionTargetService
{
    public ValueTask<AgentExecutionTargetDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = target.Descriptor;
        if (descriptor.Facets.Count == 0)
        {
            var facets = new (Type Contract, string Id)[]
            {
                (typeof(IAgentProcessExecutionTarget), AgentExecutionFacetIds.ProcessExecution),
                (typeof(IAgentExecutionResourceResolver), AgentExecutionFacetIds.ResourceResolution),
                (typeof(IAgentResourceAuthorityExecutionTarget), AgentExecutionFacetIds.ResourceAuthority),
                (typeof(IAgentExecutionScopeProvider), AgentExecutionFacetIds.ExecutionScope),
                (typeof(IAgentExecutionPathMapper), AgentExecutionFacetIds.PathMapping),
                (typeof(IAgentExecutionPathEnvironment), AgentExecutionFacetIds.PathEnvironment),
                (typeof(IAgentRangedFileExecutionTarget), AgentExecutionFacetIds.RangedFileRead),
                (typeof(IAgentStructuredFileSearchExecutionTarget), AgentExecutionFacetIds.StructuredFileSearch),
                (typeof(IAgentScopedInstructionDiscoveryTarget), AgentExecutionFacetIds.ScopedInstructionDiscovery),
            };
            descriptor = descriptor with { Facets = facets.Where(facet => facet.Contract.IsInstanceOfType(target)).Select(facet => facet.Id).ToArray() };
        }
        return ValueTask.FromResult(descriptor);
    }

    public ValueTask<string?> ConfigurationGenerationAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        => target.GetConfigurationGenerationAsync(context, cancellationToken);
    public ValueTask<AgentExecutionTargetReadiness> ReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        => target.GetReadinessAsync(context, cancellationToken);
    public ValueTask<AgentExecutionShellDescriptor> ShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        => target.GetShellAsync(context, cancellationToken);
    public ValueTask<AgentResolvedResource> ResolveFileAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
        => target.ResolveFileResourceAsync(context, path, cancellationToken);
    public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
        => target.ExecuteShellAsync(context, request, cancellationToken);
    public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
        => target.ReadFileAsync(context, request, cancellationToken);
    public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
        => target.WriteFileAsync(context, request, cancellationToken);
    public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
        => target.DeleteFileAsync(context, request, cancellationToken);
    public ValueTask<AgentShellCommandResult> ExecuteProcessAsync(AgentExecutionTargetContext context, AgentProcessCommandRequest request, CancellationToken cancellationToken = default)
        => Require<IAgentProcessExecutionTarget>(AgentExecutionFacetIds.ProcessExecution).ExecuteProcessAsync(context, request, cancellationToken);
    public ValueTask<IReadOnlyList<AgentResolvedExecutionResource>> ResolveResourcesAsync(AgentExecutionTargetContext context,
        IReadOnlyList<AgentExecutionResourceDescriptor> resources, CancellationToken cancellationToken = default)
        => Require<IAgentExecutionResourceResolver>(AgentExecutionFacetIds.ResourceResolution).ResolveResourcesAsync(context, resources, cancellationToken);
    public ValueTask<AgentResourceAuthorityValidation> ValidateResourceAuthorityAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        => Require<IAgentResourceAuthorityExecutionTarget>(AgentExecutionFacetIds.ResourceAuthority).ValidateResourceAuthorityAsync(context, cancellationToken);
    public ValueTask ReleaseResourceAuthorityAsync(IReadOnlyList<string> capabilities, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require<IAgentResourceAuthorityExecutionTarget>(AgentExecutionFacetIds.ResourceAuthority).ReleaseResourceAuthority(capabilities);
        return ValueTask.CompletedTask;
    }
    public ValueTask<AgentExecutionScopeDescriptor> ExecutionScopeAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        => Require<IAgentExecutionScopeProvider>(AgentExecutionFacetIds.ExecutionScope).GetExecutionScopeAsync(context, cancellationToken);
    public ValueTask<AgentExecutionPathMapping> MapPathAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
        => Require<IAgentExecutionPathMapper>(AgentExecutionFacetIds.PathMapping).MapToHostPathAsync(context, path, cancellationToken);
    public ValueTask<IReadOnlyList<string>> ListPathAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        => Require<IAgentExecutionPathEnvironment>(AgentExecutionFacetIds.PathEnvironment).ListPathEntriesAsync(context, cancellationToken);
    public ValueTask AddPathAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
        => Require<IAgentExecutionPathEnvironment>(AgentExecutionFacetIds.PathEnvironment).AddPathEntryAsync(context, path, cancellationToken);
    public ValueTask<AgentFileSearchResult> StructuredFileSearchAsync(AgentExecutionTargetContext context, AgentFileSearchRequest request, CancellationToken cancellationToken = default)
        => Require<IAgentStructuredFileSearchExecutionTarget>(AgentExecutionFacetIds.StructuredFileSearch).ExecuteFileSearchAsync(context, request, cancellationToken);
    public ValueTask<AgentScopedInstructionDiscoveryResult> DiscoverInstructionsAsync(AgentExecutionTargetContext context,
        AgentScopedInstructionDiscoveryRequest request, CancellationToken cancellationToken = default)
        => Require<IAgentScopedInstructionDiscoveryTarget>(AgentExecutionFacetIds.ScopedInstructionDiscovery).DiscoverScopedInstructionsAsync(context, request, cancellationToken);

    private T Require<T>(string facet) where T : class
        => target is T value && (target.Descriptor.Facets.Count == 0 || target.Descriptor.SupportsFacet(facet)) ? value
            : throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "agent.execution.facet-unavailable", $"Execution target facet '{facet}' is unavailable."));
}
