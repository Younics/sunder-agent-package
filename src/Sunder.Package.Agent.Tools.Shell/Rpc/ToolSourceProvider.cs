using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Tool_Source;

namespace Sunder.Package.Agent.Tools.Shell.Rpc;

[SunderRpcProvider(typeof(IToolsService), ProviderId = "shell.tools")]
internal sealed partial class ToolSourceProvider(ShellToolSource source, ShellExecutionBindings execution) : IToolsService
{
    public ValueTask<Wire.ToolSourceDescription> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new Wire.ToolSourceDescription { SourceId = source.SourceId, DisplayName = source.DisplayName, SourceKind = source.SourceKind }); }
    public async ValueTask<IReadOnlyList<Wire.AgentToolDescriptor>> ListToolsAsync(SunderRpcInvocationContext context, Wire.AgentToolSourceContextWire request, CancellationToken token)
        => ShellProviderValues.Convert<Wire.AgentToolDescriptor[]>(await source.ListToolsAsync(SourceContext(request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentToolReadiness?> GetReadinessAsync(SunderRpcInvocationContext context, Wire.ToolReadinessRequest request, CancellationToken token)
        => ShellProviderValues.Optional<Wire.AgentToolReadiness>(await source.GetReadinessAsync(request.ToolId, SourceContext(request.Context),
            execution.Select(ShellProviderValues.Optional<AgentRpcProviderHandle>(request.Context.ExecutionTarget), context.DeadlineUtc), token).ConfigureAwait(false));
    public ValueTask<Wire.AgentToolResult?> PreflightAsync(SunderRpcInvocationContext context, Wire.ToolInvocationRequest request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<Wire.AgentToolResult?>(null); }
    public async ValueTask<Wire.AgentToolResult> ExecuteAsync(SunderRpcInvocationContext context, Wire.ToolInvocationRequest request, CancellationToken token)
        => ShellProviderValues.Convert<Wire.AgentToolResult>(await source.ExecuteAsync(ExecutionContext(request.Context),
            ShellProviderValues.Convert<Domain.AgentToolRequest>(request.Request),
            execution.Select(ShellProviderValues.Optional<AgentRpcProviderHandle>(request.Context.ExecutionTarget), context.DeadlineUtc), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentPermissionRequestWire?> PermissionAsync(SunderRpcInvocationContext context, Wire.ToolInvocationRequest request, CancellationToken token)
    {
        var value = await source.BuildPermissionRequestAsync(ExecutionContext(request.Context), ShellProviderValues.Convert<Domain.AgentToolRequest>(request.Request), token).ConfigureAwait(false);
        return value is null ? null : new() { Request = ShellProviderValues.Convert<Wire.AgentPermissionRequest>(value), ResourceCapabilities = value.ResourceCapabilities };
    }
    public ValueTask<Wire.AgentToolPresentation?> PresentationAsync(SunderRpcInvocationContext context, Wire.AgentToolPresentationRequest request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(ShellProviderValues.Optional<Wire.AgentToolPresentation>(source.ResolveToolPresentation(ShellProviderValues.Convert<Domain.AgentToolPresentationRequest>(request)))); }
    private static Domain.AgentToolSourceContext SourceContext(Wire.AgentToolSourceContextWire value)
        => ShellProviderValues.Convert<Domain.AgentToolSourceContext>(value.Context) with { ExecutionTargetConfigurationGeneration = value.ExecutionTargetConfigurationGeneration };
    private static Domain.AgentToolExecutionContext ExecutionContext(Wire.AgentToolExecutionContextWire value)
        => ShellProviderValues.Convert<Domain.AgentToolExecutionContext>(value.Context) with
        {
            ExecutionTargetConfigurationGeneration = value.ExecutionTargetConfigurationGeneration,
            ApprovedResourceCapabilities = value.ApprovedResourceCapabilities,
            ResourceOperation = value.ResourceOperation is { } operation ? new Domain.AgentResourceOperationContext(operation.RunId, operation.RunRevision,
                operation.ToolCallId, operation.ActionId, checked((int)operation.ResourceIndex), operation.WorkspaceGeneration, operation.BindingGeneration,
                operation.ToolOwnerPackageId, operation.ExecutionTargetOwnerPackageId, operation.AuthorityActivationId, checked((int)operation.AuthorityUseCount), operation.CanIssueOutsideAuthority) : null,
        };
}
