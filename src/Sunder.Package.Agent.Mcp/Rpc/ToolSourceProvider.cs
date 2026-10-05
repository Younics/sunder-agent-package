using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Tool_Source;

namespace Sunder.Package.Agent.Mcp.Rpc;

[SunderRpcProvider(typeof(IToolsService), ProviderId = "mcp.tools")]
public sealed partial class ToolSourceProvider(IAgentToolSource source) : IToolsService
{
    public ValueTask<Wire.ToolSourceDescription> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new Wire.ToolSourceDescription { SourceId = source.SourceId, DisplayName = source.DisplayName, SourceKind = source.SourceKind }); }
    public async ValueTask<IReadOnlyList<Wire.AgentToolDescriptor>> ListToolsAsync(SunderRpcInvocationContext context, Wire.AgentToolSourceContextWire request, CancellationToken token)
        => McpProviderValues.Convert<Wire.AgentToolDescriptor[]>(await source.ListToolsAsync(SourceContext(request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentToolReadiness?> GetReadinessAsync(SunderRpcInvocationContext context, Wire.ToolReadinessRequest request, CancellationToken token)
        => McpProviderValues.Optional<Wire.AgentToolReadiness>(await source.GetReadinessAsync(request.ToolId, SourceContext(request.Context), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentToolResult?> PreflightAsync(SunderRpcInvocationContext context, Wire.ToolInvocationRequest request, CancellationToken token)
        => source is IAgentToolExecutionPreflightSource preflight ? McpProviderValues.Optional<Wire.AgentToolResult>(await preflight.PreflightExecutionAsync(
            ExecutionContext(request.Context), McpProviderValues.Convert<Domain.AgentToolRequest>(request.Request), token).ConfigureAwait(false)) : null;
    public async ValueTask<Wire.AgentToolResult> ExecuteAsync(SunderRpcInvocationContext context, Wire.ToolInvocationRequest request, CancellationToken token)
        => McpProviderValues.Convert<Wire.AgentToolResult>(await source.ExecuteAsync(ExecutionContext(request.Context), McpProviderValues.Convert<Domain.AgentToolRequest>(request.Request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentPermissionRequestWire?> PermissionAsync(SunderRpcInvocationContext context, Wire.ToolInvocationRequest request, CancellationToken token)
    {
        if (source is not IAgentPermissionAwareToolSource permission) return null;
        var value = await permission.BuildPermissionRequestAsync(ExecutionContext(request.Context), McpProviderValues.Convert<Domain.AgentToolRequest>(request.Request), token).ConfigureAwait(false);
        return value is null ? null : new() { Request = McpProviderValues.Convert<Wire.AgentPermissionRequest>(value), ResourceCapabilities = value.ResourceCapabilities };
    }
    public ValueTask<Wire.AgentToolPresentation?> PresentationAsync(SunderRpcInvocationContext context, Wire.AgentToolPresentationRequest request, CancellationToken token)
        => ValueTask.FromResult(source is IAgentToolPresentationResolver presentation
            ? McpProviderValues.Optional<Wire.AgentToolPresentation>(presentation.ResolveToolPresentation(McpProviderValues.Convert<Domain.AgentToolPresentationRequest>(request))) : null);
    private static Domain.AgentToolSourceContext SourceContext(Wire.AgentToolSourceContextWire value)
        => McpProviderValues.Convert<Domain.AgentToolSourceContext>(value.Context) with { ExecutionTargetConfigurationGeneration = value.ExecutionTargetConfigurationGeneration };
    internal static Domain.AgentToolExecutionContext ExecutionContext(Wire.AgentToolExecutionContextWire value)
        => McpProviderValues.Convert<Domain.AgentToolExecutionContext>(value.Context) with
        {
            ExecutionTargetConfigurationGeneration = value.ExecutionTargetConfigurationGeneration,
            ApprovedResourceCapabilities = value.ApprovedResourceCapabilities,
            ResourceOperation = value.ResourceOperation is { } operation ? new Domain.AgentResourceOperationContext(operation.RunId, operation.RunRevision,
                operation.ToolCallId, operation.ActionId, checked((int)operation.ResourceIndex), operation.WorkspaceGeneration, operation.BindingGeneration,
                operation.ToolOwnerPackageId, operation.ExecutionTargetOwnerPackageId, operation.AuthorityActivationId, checked((int)operation.AuthorityUseCount), operation.CanIssueOutsideAuthority) : null,
        };
}
