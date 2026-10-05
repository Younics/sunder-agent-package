using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Tool_Source;

namespace Sunder.Package.Agent.Tools.Files.Rpc;

[SunderRpcProvider(typeof(IToolsService), ProviderId = "files.tools")]
internal sealed partial class ToolSourceProvider(FilesToolSource source, FilesExecutionBindings execution) : IToolsService
{
    public ValueTask<Wire.ToolSourceDescription> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new Wire.ToolSourceDescription { SourceId = source.SourceId, DisplayName = source.DisplayName, SourceKind = source.SourceKind }); }
    public async ValueTask<IReadOnlyList<Wire.AgentToolDescriptor>> ListToolsAsync(SunderRpcInvocationContext context, Wire.AgentToolSourceContextWire request, CancellationToken token)
        => FilesProviderValues.Convert<Wire.AgentToolDescriptor[]>(await source.ListToolsAsync(SourceContext(request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentToolReadiness?> GetReadinessAsync(SunderRpcInvocationContext context, Wire.ToolReadinessRequest request, CancellationToken token)
        => FilesProviderValues.Optional<Wire.AgentToolReadiness>(await source.GetReadinessAsync(request.ToolId, SourceContext(request.Context),
            execution.Select(FilesProviderValues.Optional<AgentRpcProviderHandle>(request.Context.ExecutionTarget), context.DeadlineUtc), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentToolResult?> PreflightAsync(SunderRpcInvocationContext context, Wire.ToolInvocationRequest request, CancellationToken token)
        => FilesProviderValues.Optional<Wire.AgentToolResult>(await source.PreflightExecutionAsync(ExecutionContext(request.Context),
            FilesProviderValues.Convert<Domain.AgentToolRequest>(request.Request), Selected(request, context), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentToolResult> ExecuteAsync(SunderRpcInvocationContext context, Wire.ToolInvocationRequest request, CancellationToken token)
        => FilesProviderValues.Convert<Wire.AgentToolResult>(await source.ExecuteAsync(ExecutionContext(request.Context),
            FilesProviderValues.Convert<Domain.AgentToolRequest>(request.Request), Selected(request, context), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentPermissionRequestWire?> PermissionAsync(SunderRpcInvocationContext context, Wire.ToolInvocationRequest request, CancellationToken token)
    {
        var value = await source.BuildPermissionRequestAsync(ExecutionContext(request.Context), FilesProviderValues.Convert<Domain.AgentToolRequest>(request.Request), Selected(request, context), token).ConfigureAwait(false);
        return value is null ? null : new() { Request = FilesProviderValues.Convert<Wire.AgentPermissionRequest>(value), ResourceCapabilities = value.ResourceCapabilities };
    }
    public ValueTask<Wire.AgentToolPresentation?> PresentationAsync(SunderRpcInvocationContext context, Wire.AgentToolPresentationRequest request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(FilesProviderValues.Optional<Wire.AgentToolPresentation>(source.ResolveToolPresentation(FilesProviderValues.Convert<Domain.AgentToolPresentationRequest>(request)))); }
    private FileExecutionReference? Selected(Wire.ToolInvocationRequest request, SunderRpcInvocationContext context)
        => execution.Select(FilesProviderValues.Optional<AgentRpcProviderHandle>(request.Context.ExecutionTarget), context.DeadlineUtc);
    private static Domain.AgentToolSourceContext SourceContext(Wire.AgentToolSourceContextWire value)
        => FilesProviderValues.Convert<Domain.AgentToolSourceContext>(value.Context) with { ExecutionTargetConfigurationGeneration = value.ExecutionTargetConfigurationGeneration };
    private static Domain.AgentToolExecutionContext ExecutionContext(Wire.AgentToolExecutionContextWire value)
        => FilesProviderValues.Convert<Domain.AgentToolExecutionContext>(value.Context) with
        {
            ExecutionTargetConfigurationGeneration = value.ExecutionTargetConfigurationGeneration,
            ApprovedResourceCapabilities = value.ApprovedResourceCapabilities,
            ResourceOperation = value.ResourceOperation is { } operation ? new Domain.AgentResourceOperationContext(operation.RunId, operation.RunRevision,
                operation.ToolCallId, operation.ActionId, checked((int)operation.ResourceIndex), operation.WorkspaceGeneration, operation.BindingGeneration,
                operation.ToolOwnerPackageId, operation.ExecutionTargetOwnerPackageId, operation.AuthorityActivationId, checked((int)operation.AuthorityUseCount), operation.CanIssueOutsideAuthority) : null,
        };
}
