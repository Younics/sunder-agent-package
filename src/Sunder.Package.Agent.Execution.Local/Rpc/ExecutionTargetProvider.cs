using Sunder.Generated.Rpc;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Execution_Target;

namespace Sunder.Package.Agent.Execution.Local.Rpc;

[SunderRpcProvider(typeof(IExecutionTargetService), ProviderId = "local.execution.target")]
public sealed partial class ExecutionTargetProvider(LocalExecutionTarget target) : IExecutionTargetService
{
    public ValueTask<Wire.AgentExecutionTargetDescriptor> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(ExecutionValues.Convert<Wire.AgentExecutionTargetDescriptor>(target.Descriptor)); }
    public ValueTask<string?> ConfigurationGenerationAsync(SunderRpcInvocationContext context, Wire.AgentExecutionTargetContextWire request, CancellationToken token)
        => target.GetConfigurationGenerationAsync(ExecutionValues.Context(request), token);
    public async ValueTask<Wire.AgentExecutionTargetReadiness> ReadinessAsync(SunderRpcInvocationContext context, Wire.AgentExecutionTargetContextWire request, CancellationToken token)
        => ExecutionValues.Convert<Wire.AgentExecutionTargetReadiness>(await target.GetReadinessAsync(ExecutionValues.Context(request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentExecutionShellDescriptor> ShellAsync(SunderRpcInvocationContext context, Wire.AgentExecutionTargetContextWire request, CancellationToken token)
        => ExecutionValues.Convert<Wire.AgentExecutionShellDescriptor>(await target.GetShellAsync(ExecutionValues.Context(request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentResolvedResourceWire> ResolveFileAsync(SunderRpcInvocationContext context, Wire.ExecutionPathRequest request, CancellationToken token)
        => ExecutionValues.Resource(await target.ResolveFileResourceAsync(ExecutionValues.Context(request.Context), request.Path, token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentShellCommandResult> ExecuteShellAsync(SunderRpcInvocationContext context, Wire.ExecutionShellRequest request, CancellationToken token)
        => ExecutionValues.Convert<Wire.AgentShellCommandResult>(await target.ExecuteShellAsync(ExecutionValues.Context(request.Context), ExecutionValues.Convert<Domain.AgentShellCommandRequest>(request.Request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentFileReadResult> ReadFileAsync(SunderRpcInvocationContext context, Wire.ExecutionFileReadRequest request, CancellationToken token)
        => ExecutionValues.Convert<Wire.AgentFileReadResult>(await target.ReadFileAsync(ExecutionValues.Context(request.Context), ExecutionValues.Convert<Domain.AgentFileReadRequest>(request.Request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentFileMutationResultWire> WriteFileAsync(SunderRpcInvocationContext context, Wire.ExecutionFileWriteRequest request, CancellationToken token)
        => ExecutionValues.Mutation(await target.WriteFileAsync(ExecutionValues.Context(request.Context), ExecutionValues.Convert<Domain.AgentFileWriteRequest>(request.Request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentFileMutationResultWire> DeleteFileAsync(SunderRpcInvocationContext context, Wire.ExecutionFileDeleteRequest request, CancellationToken token)
        => ExecutionValues.Mutation(await target.DeleteFileAsync(ExecutionValues.Context(request.Context), ExecutionValues.Convert<Domain.AgentFileDeleteRequest>(request.Request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentShellCommandResult> ExecuteProcessAsync(SunderRpcInvocationContext context, Wire.ExecutionProcessRequest request, CancellationToken token)
        => ExecutionValues.Convert<Wire.AgentShellCommandResult>(await target.ExecuteProcessAsync(ExecutionValues.Context(request.Context), ExecutionValues.Convert<Domain.AgentProcessCommandRequest>(request.Request), token).ConfigureAwait(false));
    public async ValueTask<IReadOnlyList<Wire.AgentResolvedExecutionResource>> ResolveResourcesAsync(SunderRpcInvocationContext context, Wire.ExecutionResourcesRequest request, CancellationToken token)
        => ExecutionValues.Convert<Wire.AgentResolvedExecutionResource[]>(await target.ResolveResourcesAsync(ExecutionValues.Context(request.Context), ExecutionValues.Convert<Domain.AgentExecutionResourceDescriptor[]>(request.Resources), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentResourceAuthorityValidation> ValidateResourceAuthorityAsync(SunderRpcInvocationContext context, Wire.AgentExecutionTargetContextWire request, CancellationToken token)
        => ExecutionValues.Convert<Wire.AgentResourceAuthorityValidation>(await target.ValidateResourceAuthorityAsync(ExecutionValues.Context(request), token).ConfigureAwait(false));
    public ValueTask<Wire.Unit> ReleaseResourceAuthorityAsync(SunderRpcInvocationContext context, Wire.ResourceCapabilitiesRequest request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); target.ReleaseResourceAuthority(request.Capabilities); return ValueTask.FromResult(new Wire.Unit()); }
    public async ValueTask<Wire.AgentExecutionScopeDescriptor> ExecutionScopeAsync(SunderRpcInvocationContext context, Wire.AgentExecutionTargetContextWire request, CancellationToken token)
        => ExecutionValues.Convert<Wire.AgentExecutionScopeDescriptor>(await target.GetExecutionScopeAsync(ExecutionValues.Context(request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentExecutionPathMapping> MapPathAsync(SunderRpcInvocationContext context, Wire.ExecutionPathRequest request, CancellationToken token)
        => ExecutionValues.Convert<Wire.AgentExecutionPathMapping>(await target.MapToHostPathAsync(ExecutionValues.Context(request.Context), request.Path, token).ConfigureAwait(false));
    public ValueTask<IReadOnlyList<string>> ListPathAsync(SunderRpcInvocationContext context, Wire.AgentExecutionTargetContextWire request, CancellationToken token)
        => target.ListPathEntriesAsync(ExecutionValues.Context(request), token);
    public async ValueTask<Wire.Unit> AddPathAsync(SunderRpcInvocationContext context, Wire.ExecutionPathRequest request, CancellationToken token)
    { await target.AddPathEntryAsync(ExecutionValues.Context(request.Context), request.Path, token).ConfigureAwait(false); return new(); }
    public async ValueTask<Wire.AgentFileSearchResult> StructuredFileSearchAsync(SunderRpcInvocationContext context, Wire.ExecutionFileSearchRequest request, CancellationToken token)
        => ExecutionValues.Convert<Wire.AgentFileSearchResult>(await target.ExecuteFileSearchAsync(ExecutionValues.Context(request.Context), ExecutionValues.Convert<Domain.AgentFileSearchRequest>(request.Request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentScopedInstructionDiscoveryResult> DiscoverInstructionsAsync(SunderRpcInvocationContext context, Wire.ExecutionInstructionDiscoveryRequest request, CancellationToken token)
        => ExecutionValues.Convert<Wire.AgentScopedInstructionDiscoveryResult>(await target.DiscoverScopedInstructionsAsync(ExecutionValues.Context(request.Context), ExecutionValues.Convert<Domain.AgentScopedInstructionDiscoveryRequest>(request.Request), token).ConfigureAwait(false));
}
