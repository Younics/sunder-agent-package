using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.ExecutionTarget;

namespace Sunder.Package.Agent.Tests;

// Arbitrary controllable execution collaborators used by race/authority tests.
// The real source-generated interface and handler own wire dispatch/validation.
internal sealed class RegressionTestExecutionProvider(IAgentExecutionTarget target) : Wire.IExecutionTargetProvider
{
    public ValueTask<Wire.AgentExecutionTargetDescriptor> DescribeAsync(SunderRpcInvocationContext invocation, Wire.Unit request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
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
        return ValueTask.FromResult(RegressionTestRpcValues.Convert<Wire.AgentExecutionTargetDescriptor>(descriptor));
    }

    public ValueTask<string?> ConfigurationGenerationAsync(SunderRpcInvocationContext invocation, Wire.AgentExecutionTargetContextWire request, CancellationToken token = default)
        => target.GetConfigurationGenerationAsync(Context(request), token);
    public async ValueTask<Wire.AgentExecutionTargetReadiness> ReadinessAsync(SunderRpcInvocationContext invocation, Wire.AgentExecutionTargetContextWire request, CancellationToken token = default)
        => RegressionTestRpcValues.Convert<Wire.AgentExecutionTargetReadiness>(await target.GetReadinessAsync(Context(request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentExecutionShellDescriptor> ShellAsync(SunderRpcInvocationContext invocation, Wire.AgentExecutionTargetContextWire request, CancellationToken token = default)
        => RegressionTestRpcValues.Convert<Wire.AgentExecutionShellDescriptor>(await target.GetShellAsync(Context(request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentResolvedResourceWire> ResolveFileAsync(SunderRpcInvocationContext invocation, Wire.ExecutionPathRequest request, CancellationToken token = default)
        => Resource(await target.ResolveFileResourceAsync(Context(request.Context), request.Path, token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentShellCommandResult> ExecuteShellAsync(SunderRpcInvocationContext invocation, Wire.ExecutionShellRequest request, CancellationToken token = default)
        => RegressionTestRpcValues.Convert<Wire.AgentShellCommandResult>(await target.ExecuteShellAsync(Context(request.Context), RegressionTestRpcValues.Convert<Domain.AgentShellCommandRequest>(request.Request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentFileReadResult> ReadFileAsync(SunderRpcInvocationContext invocation, Wire.ExecutionFileReadRequest request, CancellationToken token = default)
        => RegressionTestRpcValues.Convert<Wire.AgentFileReadResult>(await target.ReadFileAsync(Context(request.Context), RegressionTestRpcValues.Convert<Domain.AgentFileReadRequest>(request.Request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentFileMutationResultWire> WriteFileAsync(SunderRpcInvocationContext invocation, Wire.ExecutionFileWriteRequest request, CancellationToken token = default)
        => Mutation(await target.WriteFileAsync(Context(request.Context), RegressionTestRpcValues.Convert<Domain.AgentFileWriteRequest>(request.Request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentFileMutationResultWire> DeleteFileAsync(SunderRpcInvocationContext invocation, Wire.ExecutionFileDeleteRequest request, CancellationToken token = default)
        => Mutation(await target.DeleteFileAsync(Context(request.Context), RegressionTestRpcValues.Convert<Domain.AgentFileDeleteRequest>(request.Request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentShellCommandResult> ExecuteProcessAsync(SunderRpcInvocationContext invocation, Wire.ExecutionProcessRequest request, CancellationToken token = default)
        => RegressionTestRpcValues.Convert<Wire.AgentShellCommandResult>(await Require<IAgentProcessExecutionTarget>(AgentExecutionFacetIds.ProcessExecution)
            .ExecuteProcessAsync(Context(request.Context), RegressionTestRpcValues.Convert<Domain.AgentProcessCommandRequest>(request.Request), token).ConfigureAwait(false));
    public async ValueTask<IReadOnlyList<Wire.AgentResolvedExecutionResource>> ResolveResourcesAsync(SunderRpcInvocationContext invocation, Wire.ExecutionResourcesRequest request, CancellationToken token = default)
        => RegressionTestRpcValues.Convert<Wire.AgentResolvedExecutionResource[]>(await Require<IAgentExecutionResourceResolver>(AgentExecutionFacetIds.ResourceResolution)
            .ResolveResourcesAsync(Context(request.Context), RegressionTestRpcValues.Convert<Domain.AgentExecutionResourceDescriptor[]>(request.Resources), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentResourceAuthorityValidation> ValidateResourceAuthorityAsync(SunderRpcInvocationContext invocation, Wire.AgentExecutionTargetContextWire request, CancellationToken token = default)
        => RegressionTestRpcValues.Convert<Wire.AgentResourceAuthorityValidation>(await Require<IAgentResourceAuthorityExecutionTarget>(AgentExecutionFacetIds.ResourceAuthority)
            .ValidateResourceAuthorityAsync(Context(request), token).ConfigureAwait(false));
    public ValueTask<Wire.Unit> ReleaseResourceAuthorityAsync(SunderRpcInvocationContext invocation, Wire.ResourceCapabilitiesRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Require<IAgentResourceAuthorityExecutionTarget>(AgentExecutionFacetIds.ResourceAuthority).ReleaseResourceAuthority(request.Capabilities);
        return ValueTask.FromResult(new Wire.Unit());
    }
    public async ValueTask<Wire.AgentExecutionScopeDescriptor> ExecutionScopeAsync(SunderRpcInvocationContext invocation, Wire.AgentExecutionTargetContextWire request, CancellationToken token = default)
        => RegressionTestRpcValues.Convert<Wire.AgentExecutionScopeDescriptor>(await Require<IAgentExecutionScopeProvider>(AgentExecutionFacetIds.ExecutionScope).GetExecutionScopeAsync(Context(request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentExecutionPathMapping> MapPathAsync(SunderRpcInvocationContext invocation, Wire.ExecutionPathRequest request, CancellationToken token = default)
        => RegressionTestRpcValues.Convert<Wire.AgentExecutionPathMapping>(await Require<IAgentExecutionPathMapper>(AgentExecutionFacetIds.PathMapping).MapToHostPathAsync(Context(request.Context), request.Path, token).ConfigureAwait(false));
    public ValueTask<IReadOnlyList<string>> ListPathAsync(SunderRpcInvocationContext invocation, Wire.AgentExecutionTargetContextWire request, CancellationToken token = default)
        => Require<IAgentExecutionPathEnvironment>(AgentExecutionFacetIds.PathEnvironment).ListPathEntriesAsync(Context(request), token);
    public async ValueTask<Wire.Unit> AddPathAsync(SunderRpcInvocationContext invocation, Wire.ExecutionPathRequest request, CancellationToken token = default)
    {
        await Require<IAgentExecutionPathEnvironment>(AgentExecutionFacetIds.PathEnvironment).AddPathEntryAsync(Context(request.Context), request.Path, token).ConfigureAwait(false);
        return new();
    }
    public async ValueTask<Wire.AgentFileSearchResult> StructuredFileSearchAsync(SunderRpcInvocationContext invocation, Wire.ExecutionFileSearchRequest request, CancellationToken token = default)
        => RegressionTestRpcValues.Convert<Wire.AgentFileSearchResult>(await Require<IAgentStructuredFileSearchExecutionTarget>(AgentExecutionFacetIds.StructuredFileSearch)
            .ExecuteFileSearchAsync(Context(request.Context), RegressionTestRpcValues.Convert<Domain.AgentFileSearchRequest>(request.Request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentScopedInstructionDiscoveryResult> DiscoverInstructionsAsync(SunderRpcInvocationContext invocation, Wire.ExecutionInstructionDiscoveryRequest request, CancellationToken token = default)
        => RegressionTestRpcValues.Convert<Wire.AgentScopedInstructionDiscoveryResult>(await Require<IAgentScopedInstructionDiscoveryTarget>(AgentExecutionFacetIds.ScopedInstructionDiscovery)
            .DiscoverScopedInstructionsAsync(Context(request.Context), RegressionTestRpcValues.Convert<Domain.AgentScopedInstructionDiscoveryRequest>(request.Request), token).ConfigureAwait(false));

    private T Require<T>(string facet) where T : class
        => target is T value && (target.Descriptor.Facets.Count == 0 || target.Descriptor.SupportsFacet(facet)) ? value
            : throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "agent.execution.facet-unavailable", $"Execution target facet '{facet}' is unavailable."));

    internal static Domain.AgentExecutionTargetContext Context(Wire.AgentExecutionTargetContextWire value)
        => RegressionTestRpcValues.Convert<Domain.AgentExecutionTargetContext>(value.Context) with
        {
            ApprovedResourceCapabilities = value.ApprovedResourceCapabilities,
            CapturePostMutationResource = value.CapturePostMutationResource,
            ExpectedConfigurationGeneration = value.ExpectedConfigurationGeneration,
            ResourceOperation = value.ResourceOperation is { } operation ? new(operation.RunId, operation.RunRevision, operation.ToolCallId,
                operation.ActionId, checked((int)operation.ResourceIndex), operation.WorkspaceGeneration, operation.BindingGeneration,
                operation.ToolOwnerPackageId, operation.ExecutionTargetOwnerPackageId, operation.AuthorityActivationId,
                checked((int)operation.AuthorityUseCount), operation.CanIssueOutsideAuthority) : null,
        };

    internal static Wire.AgentResolvedResourceWire Resource(Domain.AgentResolvedResource value)
        => new() { Resource = RegressionTestRpcValues.Convert<Wire.AgentResolvedResource>(value), AuthorityReferences = value.AuthorityReferences, DeleteAuthorityReferences = value.DeleteAuthorityReferences };
    internal static Wire.AgentFileMutationResultWire Mutation(Domain.AgentFileMutationResult value)
        => new() { Result = RegressionTestRpcValues.Convert<Wire.AgentFileMutationResult>(value), PostMutationResource = value.PostMutationResource is { } resource ? Resource(resource) : null };
}
