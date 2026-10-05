using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Wire = Sunder.Generated.Rpc.ExecutionTarget;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed partial class GeneratedExecutionTargetClient
{
    public ValueTask<AgentShellCommandResult> ExecuteProcessAsync(AgentExecutionTargetContext context, AgentProcessCommandRequest request, CancellationToken cancellationToken = default)
        => InvokeFacet(AgentExecutionFacetIds.ProcessExecution, async (client, token) => AgentConsumerValues.Convert<AgentShellCommandResult>(
            await client.ExecuteProcessAsync(new() { Context = AgentExecutionValues.Context(context), Request = AgentConsumerValues.Convert<Wire.AgentProcessCommandRequest>(request) }, token).ConfigureAwait(false)), cancellationToken);
    public ValueTask<IReadOnlyList<AgentResolvedExecutionResource>> ResolveResourcesAsync(AgentExecutionTargetContext context, IReadOnlyList<AgentExecutionResourceDescriptor> resources, CancellationToken cancellationToken = default)
        => InvokeFacet<IReadOnlyList<AgentResolvedExecutionResource>>(AgentExecutionFacetIds.ResourceResolution, async (client, token) => AgentConsumerValues.Convert<AgentResolvedExecutionResource[]>(
            await client.ResolveResourcesAsync(new() { Context = AgentExecutionValues.Context(context), Resources = AgentConsumerValues.Convert<Wire.AgentExecutionResourceDescriptor[]>(resources) }, token).ConfigureAwait(false)), cancellationToken);
    public ValueTask<AgentResourceAuthorityValidation> ValidateResourceAuthorityAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        => InvokeFacet(AgentExecutionFacetIds.ResourceAuthority, async (client, token) => AgentConsumerValues.Convert<AgentResourceAuthorityValidation>(
            await client.ValidateResourceAuthorityAsync(AgentExecutionValues.Context(context), token).ConfigureAwait(false)), cancellationToken);
    public void ReleaseResourceAuthority(IReadOnlyList<string> resourceCapabilities)
        => _ = InvokeFacet(AgentExecutionFacetIds.ResourceAuthority, (client, token) => client.ReleaseResourceAuthorityAsync(new() { Capabilities = resourceCapabilities }, token), default).AsTask().GetAwaiter().GetResult();
    public ValueTask<AgentExecutionScopeDescriptor> GetExecutionScopeAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        => InvokeFacet(AgentExecutionFacetIds.ExecutionScope, async (client, token) => AgentConsumerValues.Convert<AgentExecutionScopeDescriptor>(
            await client.ExecutionScopeAsync(AgentExecutionValues.Context(context), token).ConfigureAwait(false)), cancellationToken);
    public ValueTask<AgentExecutionPathMapping> MapToHostPathAsync(AgentExecutionTargetContext context, string executionPath, CancellationToken cancellationToken = default)
        => InvokeFacet(AgentExecutionFacetIds.PathMapping, async (client, token) => AgentConsumerValues.Convert<AgentExecutionPathMapping>(
            await client.MapPathAsync(new() { Context = AgentExecutionValues.Context(context), Path = executionPath }, token).ConfigureAwait(false)), cancellationToken);
    public ValueTask<IReadOnlyList<string>> ListPathEntriesAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        => InvokeFacet(AgentExecutionFacetIds.PathEnvironment, (client, token) => client.ListPathAsync(AgentExecutionValues.Context(context), token), cancellationToken);
    public async ValueTask AddPathEntryAsync(AgentExecutionTargetContext context, string executionPath, CancellationToken cancellationToken = default)
        => _ = await InvokeFacet(AgentExecutionFacetIds.PathEnvironment, (client, token) => client.AddPathAsync(new() { Context = AgentExecutionValues.Context(context), Path = executionPath }, token), cancellationToken).ConfigureAwait(false);
    public ValueTask<AgentFileSearchResult> ExecuteFileSearchAsync(AgentExecutionTargetContext context, AgentFileSearchRequest request, CancellationToken cancellationToken = default)
        => InvokeFacet(AgentExecutionFacetIds.StructuredFileSearch, async (client, token) => AgentConsumerValues.Convert<AgentFileSearchResult>(
            await client.StructuredFileSearchAsync(new() { Context = AgentExecutionValues.Context(context), Request = AgentConsumerValues.Convert<Wire.AgentFileSearchRequest>(request) }, token).ConfigureAwait(false)), cancellationToken);
    public ValueTask<AgentScopedInstructionDiscoveryResult> DiscoverScopedInstructionsAsync(AgentExecutionTargetContext context, AgentScopedInstructionDiscoveryRequest request, CancellationToken cancellationToken = default)
        => InvokeFacet(AgentExecutionFacetIds.ScopedInstructionDiscovery, async (client, token) => AgentConsumerValues.Convert<AgentScopedInstructionDiscoveryResult>(
            await client.DiscoverInstructionsAsync(new() { Context = AgentExecutionValues.Context(context), Request = AgentConsumerValues.Convert<Wire.AgentScopedInstructionDiscoveryRequest>(request) }, token).ConfigureAwait(false)), cancellationToken);
}
