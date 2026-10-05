using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.ToolSource;

namespace Sunder.Package.Agent.Tests;

internal sealed class RegressionTestToolProvider(IAgentToolSource source, AgentRpcCatalog catalog) : Wire.IToolSourceProvider
{
    public ValueTask<Wire.ToolSourceDescription> DescribeAsync(SunderRpcInvocationContext invocation, Wire.Unit request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new Wire.ToolSourceDescription { SourceId = source.SourceId, DisplayName = source.DisplayName, SourceKind = source.SourceKind });
    }
    public async ValueTask<IReadOnlyList<Wire.AgentToolDescriptor>> ListToolsAsync(SunderRpcInvocationContext invocation, Wire.AgentToolSourceContextWire request, CancellationToken token = default)
        => RegressionTestRpcValues.Convert<Wire.AgentToolDescriptor[]>(await source.ListToolsAsync(await Context(request, token).ConfigureAwait(false), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentToolReadiness?> GetReadinessAsync(SunderRpcInvocationContext invocation, Wire.ToolReadinessRequest request, CancellationToken token = default)
        => RegressionTestRpcValues.Optional<Wire.AgentToolReadiness>(await source.GetReadinessAsync(request.ToolId, await Context(request.Context, token).ConfigureAwait(false), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentToolResult> ExecuteAsync(SunderRpcInvocationContext invocation, Wire.ToolInvocationRequest request, CancellationToken token = default)
        => RegressionTestRpcValues.Convert<Wire.AgentToolResult>(await source.ExecuteAsync(await Context(request.Context, token).ConfigureAwait(false),
            RegressionTestRpcValues.Convert<AgentToolRequest>(request.Request), token).ConfigureAwait(false));
    public async ValueTask<Wire.AgentToolResult?> PreflightAsync(SunderRpcInvocationContext invocation, Wire.ToolInvocationRequest request, CancellationToken token = default)
        => source is IAgentToolExecutionPreflightSource preflight ? RegressionTestRpcValues.Optional<Wire.AgentToolResult>(await preflight.PreflightExecutionAsync(
            await Context(request.Context, token).ConfigureAwait(false), RegressionTestRpcValues.Convert<AgentToolRequest>(request.Request), token).ConfigureAwait(false)) : null;
    public async ValueTask<Wire.AgentPermissionRequestWire?> PermissionAsync(SunderRpcInvocationContext invocation, Wire.ToolInvocationRequest request, CancellationToken token = default)
    {
        if (source is not IAgentPermissionAwareToolSource permission) return null;
        var value = await permission.BuildPermissionRequestAsync(await Context(request.Context, token).ConfigureAwait(false),
            RegressionTestRpcValues.Convert<AgentToolRequest>(request.Request), token).ConfigureAwait(false);
        return value is null ? null : new() { Request = RegressionTestRpcValues.Convert<Wire.AgentPermissionRequest>(value), ResourceCapabilities = value.ResourceCapabilities };
    }
    public ValueTask<Wire.AgentToolPresentation?> PresentationAsync(SunderRpcInvocationContext invocation, Wire.AgentToolPresentationRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(source is IAgentToolPresentationResolver presentation
            ? RegressionTestRpcValues.Optional<Wire.AgentToolPresentation>(presentation.ResolveToolPresentation(RegressionTestRpcValues.Convert<AgentToolPresentationRequest>(request))) : null);
    }

    private async ValueTask<AgentToolSourceContext> Context(Wire.AgentToolSourceContextWire value, CancellationToken token)
        => RegressionTestRpcValues.Convert<AgentToolSourceContext>(value.Context) with
        {
            ExecutionTargetReference = await Target(value.ExecutionTarget, token).ConfigureAwait(false),
            ExecutionTargetConfigurationGeneration = value.ExecutionTargetConfigurationGeneration,
        };
    private async ValueTask<AgentToolExecutionContext> Context(Wire.AgentToolExecutionContextWire value, CancellationToken token)
        => RegressionTestRpcValues.Convert<AgentToolExecutionContext>(value.Context) with
        {
            ExecutionTargetReference = await Target(value.ExecutionTarget, token).ConfigureAwait(false),
            ExecutionTargetConfigurationGeneration = value.ExecutionTargetConfigurationGeneration,
            ApprovedResourceCapabilities = value.ApprovedResourceCapabilities,
            ResourceOperation = value.ResourceOperation is { } operation ? new(operation.RunId, operation.RunRevision, operation.ToolCallId,
                operation.ActionId, checked((int)operation.ResourceIndex), operation.WorkspaceGeneration, operation.BindingGeneration,
                operation.ToolOwnerPackageId, operation.ExecutionTargetOwnerPackageId, operation.AuthorityActivationId,
                checked((int)operation.AuthorityUseCount), operation.CanIssueOutsideAuthority) : null,
        };

    private ValueTask<AgentRpcReference<IAgentExecutionTarget>?> Target(Wire.AgentRpcProviderHandle? handle, CancellationToken token)
        => handle is null ? ValueTask.FromResult<AgentRpcReference<IAgentExecutionTarget>?>(null)
            : RegressionTestBindings.ResolveExecutionAsync(catalog, RegressionTestRpcValues.Convert<AgentRpcProviderHandle>(handle), token);
}

// AddTool's single controlled collaborator, without the retired central static
// tool adapter. Requests continue through the generated tool-source handler.
internal sealed class RegressionTestSingleToolSource(IAgentTool tool) : IAgentToolSource, IAgentPermissionAwareToolSource, IAgentToolPresentationResolver
{
    public string SourceId => "installed-packages";
    public string DisplayName => "Installed packages";
    public string SourceKind => "test";
    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>([tool.Descriptor]); }
    public async ValueTask<AgentToolReadiness?> GetReadinessAsync(string toolId, AgentToolSourceContext context, CancellationToken token = default)
        => Matches(toolId) ? await tool.GetReadinessAsync(token).ConfigureAwait(false) : null;
    public ValueTask<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken token = default)
        => Matches(request.ToolId) ? tool.ExecuteAsync(context, request, token)
            : ValueTask.FromResult(new AgentToolResult(request.ToolId, "Tool not found.", IsError: true, ErrorCode: "tool-not-found", BackendId: SourceId));
    public ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken token = default)
        => Matches(request.ToolId) && tool is IAgentPermissionAwareTool permission ? permission.BuildPermissionRequestAsync(context, request, token)
            : ValueTask.FromResult<AgentPermissionRequest?>(null);
    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
        => Matches(request.ToolId) && tool is IAgentToolPresentationResolver presentation ? presentation.ResolveToolPresentation(request) : null;
    private bool Matches(string id) => string.Equals(tool.Descriptor.ToolId, id, StringComparison.OrdinalIgnoreCase);
}
