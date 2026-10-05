using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Execution_Target;

namespace Sunder.Package.Agent.Tools.Files.Rpc;

internal sealed class ExecutionClient(FilesExecutionBindings.TargetLease lease, AgentExecutionTargetDescriptor descriptor)
    : IAgentRangedFileExecutionTarget, IAgentStructuredFileSearchExecutionTarget, IAgentScopedInstructionDiscoveryTarget,
        IAgentExecutionScopeProvider, IAgentResourceAuthorityExecutionTarget
{
    public AgentExecutionTargetDescriptor Descriptor => descriptor;
    public ValueTask<string?> GetConfigurationGenerationAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        => lease.InvokeAsync((client, token) => client.ConfigurationGenerationAsync(ExecutionValues.Context(context), token), cancellationToken);
    public async ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        => FilesProviderValues.Convert<AgentExecutionTargetReadiness>(await lease.InvokeAsync((client, token) => client.ReadinessAsync(ExecutionValues.Context(context), token), cancellationToken).ConfigureAwait(false));
    public async ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        => FilesProviderValues.Convert<AgentExecutionShellDescriptor>(await lease.InvokeAsync((client, token) => client.ShellAsync(ExecutionValues.Context(context), token), cancellationToken).ConfigureAwait(false));
    public async ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
        => ExecutionValues.Resource(await lease.InvokeAsync((client, token) => client.ResolveFileAsync(new() { Context = ExecutionValues.Context(context), Path = path }, token), cancellationToken).ConfigureAwait(false));
    public async ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
        => FilesProviderValues.Convert<AgentShellCommandResult>(await lease.InvokeAsync((client, token) => client.ExecuteShellAsync(new() { Context = ExecutionValues.Context(context), Request = FilesProviderValues.Convert<Wire.AgentShellCommandRequest>(request) }, token), cancellationToken).ConfigureAwait(false));
    public async ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
        => FilesProviderValues.Convert<AgentFileReadResult>(await lease.InvokeAsync((client, token) => client.ReadFileAsync(new() { Context = ExecutionValues.Context(context), Request = FilesProviderValues.Convert<Wire.AgentFileReadRequest>(request) }, token), cancellationToken).ConfigureAwait(false));
    public async ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
        => ExecutionValues.Mutation(await lease.InvokeAsync((client, token) => client.WriteFileAsync(new() { Context = ExecutionValues.Context(context), Request = FilesProviderValues.Convert<Wire.AgentFileWriteRequest>(request) }, token), cancellationToken).ConfigureAwait(false));
    public async ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
        => ExecutionValues.Mutation(await lease.InvokeAsync((client, token) => client.DeleteFileAsync(new() { Context = ExecutionValues.Context(context), Request = FilesProviderValues.Convert<Wire.AgentFileDeleteRequest>(request) }, token), cancellationToken).ConfigureAwait(false));
    public async ValueTask<AgentFileSearchResult> ExecuteFileSearchAsync(AgentExecutionTargetContext context, AgentFileSearchRequest request, CancellationToken cancellationToken = default)
    {
        RequireFacet(AgentExecutionFacetIds.StructuredFileSearch);
        return FilesProviderValues.Convert<AgentFileSearchResult>(await lease.InvokeAsync((client, token) => client.StructuredFileSearchAsync(new() { Context = ExecutionValues.Context(context), Request = FilesProviderValues.Convert<Wire.AgentFileSearchRequest>(request) }, token), cancellationToken).ConfigureAwait(false));
    }
    public async ValueTask<AgentScopedInstructionDiscoveryResult> DiscoverScopedInstructionsAsync(AgentExecutionTargetContext context, AgentScopedInstructionDiscoveryRequest request, CancellationToken cancellationToken = default)
    {
        RequireFacet(AgentExecutionFacetIds.ScopedInstructionDiscovery);
        return FilesProviderValues.Convert<AgentScopedInstructionDiscoveryResult>(await lease.InvokeAsync((client, token) => client.DiscoverInstructionsAsync(new() { Context = ExecutionValues.Context(context), Request = FilesProviderValues.Convert<Wire.AgentScopedInstructionDiscoveryRequest>(request) }, token), cancellationToken).ConfigureAwait(false));
    }
    public async ValueTask<AgentExecutionScopeDescriptor> GetExecutionScopeAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
    {
        RequireFacet(AgentExecutionFacetIds.ExecutionScope);
        return FilesProviderValues.Convert<AgentExecutionScopeDescriptor>(await lease.InvokeAsync((client, token) => client.ExecutionScopeAsync(ExecutionValues.Context(context), token), cancellationToken).ConfigureAwait(false));
    }
    public async ValueTask<AgentResourceAuthorityValidation> ValidateResourceAuthorityAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
    {
        RequireFacet(AgentExecutionFacetIds.ResourceAuthority);
        return FilesProviderValues.Convert<AgentResourceAuthorityValidation>(await lease.InvokeAsync((client, token) => client.ValidateResourceAuthorityAsync(ExecutionValues.Context(context), token), cancellationToken).ConfigureAwait(false));
    }
    public void ReleaseResourceAuthority(IReadOnlyList<string> resourceCapabilities)
    {
        RequireFacet(AgentExecutionFacetIds.ResourceAuthority);
        _ = lease.InvokeAsync((client, token) => client.ReleaseResourceAuthorityAsync(new() { Capabilities = resourceCapabilities }, token), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }
    private void RequireFacet(string facet)
    {
        if (!descriptor.SupportsFacet(facet)) throw new NotSupportedException($"Execution target facet '{facet}' is not advertised.");
    }
}
