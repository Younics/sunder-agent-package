using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Protocol;

public static class AgentToolSourceRpc
{
    public const string ServiceId = "tool-source";

    public static ISunderRpcServiceHandler CreateHandler(IAgentToolSource source, AgentRpcCatalog? rpcCatalog = null)
        => AgentRpcServiceHandler.Create(AgentRpcContractDescriptors.Get(AgentRpcContractIds.ToolSource))
            .AddUnary<AgentRpcEmpty, ToolSourceDescription>(ServiceId, "describe", (_, _, _) => ValueTask.FromResult(new ToolSourceDescription(source.SourceId, source.DisplayName, source.SourceKind)))
            .AddUnary<AgentToolSourceContextWire, IReadOnlyList<AgentToolDescriptor>>(ServiceId, "list-tools", async (_, request, token) => await source.ListToolsAsync(await FromWireAsync(request, rpcCatalog, token).ConfigureAwait(false), token).ConfigureAwait(false))
            .AddNullableUnary<ToolReadinessRequest, AgentToolReadiness>(ServiceId, "get-readiness", async (_, request, token) => await source.GetReadinessAsync(request.ToolId, await FromWireAsync(request.Context, rpcCatalog, token).ConfigureAwait(false), token).ConfigureAwait(false))
            .AddNullableUnary<ToolInvocationRequest, AgentToolResult>(ServiceId, "preflight", async (_, request, token) => source is IAgentToolExecutionPreflightSource preflight ? await preflight.PreflightExecutionAsync(await FromWireAsync(request.Context, rpcCatalog, token).ConfigureAwait(false), request.Request, token).ConfigureAwait(false) : null)
            .AddUnary<ToolInvocationRequest, AgentToolResult>(ServiceId, "execute", async (_, request, token) => await source.ExecuteAsync(await FromWireAsync(request.Context, rpcCatalog, token).ConfigureAwait(false), request.Request, token).ConfigureAwait(false))
            .AddNullableUnary<ToolInvocationRequest, AgentPermissionRequestWire>(ServiceId, "permission", async (_, request, token) =>
            {
                var permissionRequest = source is IAgentPermissionAwareToolSource permission
                    ? await permission.BuildPermissionRequestAsync(await FromWireAsync(request.Context, rpcCatalog, token).ConfigureAwait(false), request.Request, token).ConfigureAwait(false)
                    : null;
                return permissionRequest is null ? null : AgentPermissionRequestWire.FromRequest(permissionRequest);
            })
            .AddNullableUnary<AgentToolPresentationRequest, AgentToolPresentation>(ServiceId, "presentation", (_, request, _) => ValueTask.FromResult(source is IAgentToolPresentationResolver presentation ? presentation.ResolveToolPresentation(request) : null))
            .Build();

    private static async ValueTask<AgentToolSourceContext> FromWireAsync(
        AgentToolSourceContextWire wire,
        AgentRpcCatalog? rpcCatalog,
        CancellationToken cancellationToken)
        => wire.Context with
        {
            ExecutionTargetReference = await ResolveTargetAsync(wire.ExecutionTarget, rpcCatalog, cancellationToken)
                .ConfigureAwait(false),
            ExecutionTargetConfigurationGeneration = wire.ExecutionTargetConfigurationGeneration,
        };

    internal static async ValueTask<AgentToolExecutionContext> FromWireAsync(
        AgentToolExecutionContextWire wire,
        AgentRpcCatalog? rpcCatalog,
        CancellationToken cancellationToken)
        => wire.Context with
        {
            ExecutionTargetReference = await ResolveTargetAsync(wire.ExecutionTarget, rpcCatalog, cancellationToken)
                .ConfigureAwait(false),
            ApprovedResourceCapabilities = wire.ApprovedResourceCapabilities,
            ResourceOperation = wire.ResourceOperation?.ToContext(),
            ExecutionTargetConfigurationGeneration = wire.ExecutionTargetConfigurationGeneration,
        };

    internal static AgentToolExecutionContextWire ToWire(AgentToolExecutionContext context)
        => new(
            context,
            context.ExecutionTargetReference?.ToHandle(),
            context.ApprovedResourceCapabilities,
            AgentResourceOperationContextWire.FromContext(context.ResourceOperation),
            context.ExecutionTargetConfigurationGeneration);

    private static ValueTask<AgentRpcReference<IAgentExecutionTarget>?> ResolveTargetAsync(
        AgentRpcProviderHandle? handle,
        AgentRpcCatalog? rpcCatalog,
        CancellationToken cancellationToken)
        => handle is null || rpcCatalog is null
            ? ValueTask.FromResult<AgentRpcReference<IAgentExecutionTarget>?>(null)
            : rpcCatalog.TryGetServiceReferenceAsync(
                AgentRpcServices.ExecutionTargets,
                handle,
                cancellationToken);
}

public sealed class AgentToolSourceRpcClient :
    IAgentToolSource,
    IAgentPermissionAwareToolSource,
    IAgentToolExecutionPreflightSource,
    IAgentToolPresentationResolver
{
    private readonly ISunderRpcClient _client;
    private readonly SunderRpcEndpointReference _endpoint;
    private ToolSourceDescription? _description;

    public AgentToolSourceRpcClient(ISunderRpcClient client, SunderRpcEndpointReference endpoint)
    {
        _client = client;
        _endpoint = endpoint;
    }

    private ToolSourceDescription Description => _description ??= AgentRpcServiceHandler.InvokeAsync<AgentRpcEmpty, ToolSourceDescription>(
        _client, _endpoint, AgentToolSourceRpc.ServiceId, "describe", new AgentRpcEmpty()).AsTask().GetAwaiter().GetResult();

    public async ValueTask<ToolSourceDescription> DescribeAsync(CancellationToken cancellationToken = default)
    {
        var cached = Volatile.Read(ref _description);
        if (cached is not null)
        {
            return cached;
        }

        var description = await AgentRpcServiceHandler
            .InvokeAsync<AgentRpcEmpty, ToolSourceDescription>(
                _client,
                _endpoint,
                AgentToolSourceRpc.ServiceId,
                "describe",
                new AgentRpcEmpty(),
                cancellationToken)
            .ConfigureAwait(false);
        return Interlocked.CompareExchange(ref _description, description, null) ?? description;
    }

    public string SourceId => Description.SourceId;
    public string DisplayName => Description.DisplayName;
    public string SourceKind => Description.SourceKind;

    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken cancellationToken = default)
        => AgentRpcServiceHandler.InvokeAsync<AgentToolSourceContextWire, IReadOnlyList<AgentToolDescriptor>>(_client, _endpoint, AgentToolSourceRpc.ServiceId, "list-tools", ToWire(context), cancellationToken);

    public async ValueTask<AgentToolReadiness?> GetReadinessAsync(string toolId, AgentToolSourceContext context, CancellationToken cancellationToken = default)
        => await AgentRpcServiceHandler.InvokeNullableAsync<ToolReadinessRequest, AgentToolReadiness>(_client, _endpoint, AgentToolSourceRpc.ServiceId, "get-readiness", new(toolId, ToWire(context)), cancellationToken).ConfigureAwait(false);

    public async ValueTask<AgentToolResult?> PreflightExecutionAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
        => await AgentRpcServiceHandler.InvokeNullableAsync<ToolInvocationRequest, AgentToolResult>(_client, _endpoint, AgentToolSourceRpc.ServiceId, "preflight", new(AgentToolSourceRpc.ToWire(context), request), cancellationToken).ConfigureAwait(false);

    public ValueTask<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
        => AgentRpcServiceHandler.InvokeAsync<ToolInvocationRequest, AgentToolResult>(
            _client,
            _endpoint,
            AgentToolSourceRpc.ServiceId,
            "execute",
            new(AgentToolSourceRpc.ToWire(context), request),
            AgentRpcServiceHandler.CreateExtendedCallOptions(),
            cancellationToken);

    public async ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
        => (await AgentRpcServiceHandler.InvokeNullableAsync<ToolInvocationRequest, AgentPermissionRequestWire>(_client, _endpoint, AgentToolSourceRpc.ServiceId, "permission", new(AgentToolSourceRpc.ToWire(context), request), cancellationToken).ConfigureAwait(false))?.ToRequest();

    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
        => AgentRpcServiceHandler.InvokeNullableAsync<AgentToolPresentationRequest, AgentToolPresentation>(_client, _endpoint, AgentToolSourceRpc.ServiceId, "presentation", request).AsTask().GetAwaiter().GetResult();

    private static AgentToolSourceContextWire ToWire(AgentToolSourceContext context)
        => new(
            context,
            context.ExecutionTargetReference?.ToHandle(),
            context.ExecutionTargetConfigurationGeneration);

}

public sealed class AgentStaticToolSourceAdapter :
    IAgentToolSource,
    IAgentPermissionAwareToolSource,
    IAgentToolPresentationResolver
{
    private readonly IReadOnlyList<IAgentTool> _tools;

    public AgentStaticToolSourceAdapter(
        string sourceId,
        string displayName,
        string sourceKind,
        IEnumerable<IAgentTool> tools)
    {
        SourceId = sourceId;
        DisplayName = displayName;
        SourceKind = sourceKind;
        _tools = tools.ToArray();
    }

    public string SourceId { get; }
    public string DisplayName { get; }
    public string SourceKind { get; }

    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>(_tools.Select(static tool => tool.Descriptor).ToArray());
    }

    public ValueTask<AgentToolReadiness?> GetReadinessAsync(string toolId, AgentToolSourceContext context, CancellationToken cancellationToken = default)
    {
        var tool = Find(toolId);
        return tool is null
            ? ValueTask.FromResult<AgentToolReadiness?>(null)
            : AwaitReadiness(tool, cancellationToken);
    }

    public ValueTask<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
        => Find(request.ToolId) is { } tool
            ? tool.ExecuteAsync(context, request, cancellationToken)
            : ValueTask.FromResult(new AgentToolResult(request.ToolId, "Tool not found.", IsError: true, ErrorCode: "tool-not-found", BackendId: SourceId));

    public ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
        => Find(request.ToolId) is IAgentPermissionAwareTool permission
            ? permission.BuildPermissionRequestAsync(context, request, cancellationToken)
            : ValueTask.FromResult<AgentPermissionRequest?>(null);

    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
        => Find(request.ToolId) is IAgentToolPresentationResolver presentation
            ? presentation.ResolveToolPresentation(request)
            : null;

    private IAgentTool? Find(string toolId)
        => _tools.FirstOrDefault(tool => string.Equals(tool.Descriptor.ToolId, toolId, StringComparison.OrdinalIgnoreCase));

    private static async ValueTask<AgentToolReadiness?> AwaitReadiness(IAgentTool tool, CancellationToken cancellationToken)
        => await tool.GetReadinessAsync(cancellationToken).ConfigureAwait(false);
}

public sealed record ToolSourceDescription(string SourceId, string DisplayName, string SourceKind);
public sealed record AgentToolSourceContextWire(AgentToolSourceContext Context, AgentRpcProviderHandle? ExecutionTarget, string? ExecutionTargetConfigurationGeneration);
public sealed record AgentToolExecutionContextWire(
    AgentToolExecutionContext Context,
    AgentRpcProviderHandle? ExecutionTarget,
    IReadOnlyList<string> ApprovedResourceCapabilities,
    AgentResourceOperationContextWire? ResourceOperation,
    string? ExecutionTargetConfigurationGeneration);
public sealed record AgentResourceOperationContextWire(
    Guid RunId,
    long RunRevision,
    string ToolCallId,
    string ActionId,
    int ResourceIndex,
    string WorkspaceGeneration,
    string BindingGeneration,
    string ToolOwnerPackageId,
    string ExecutionTargetOwnerPackageId,
    string AuthorityActivationId,
    int AuthorityUseCount,
    bool CanIssueOutsideAuthority)
{
    public static AgentResourceOperationContextWire? FromContext(AgentResourceOperationContext? context)
        => context is null
            ? null
            : new(
                context.RunId,
                context.RunRevision,
                context.ToolCallId,
                context.ActionId,
                context.ResourceIndex,
                context.WorkspaceGeneration,
                context.BindingGeneration,
                context.ToolOwnerPackageId,
                context.ExecutionTargetOwnerPackageId,
                context.AuthorityActivationId,
                context.AuthorityUseCount,
                context.CanIssueOutsideAuthority);

    public AgentResourceOperationContext ToContext()
        => new(
            RunId,
            RunRevision,
            ToolCallId,
            ActionId,
            ResourceIndex,
            WorkspaceGeneration,
            BindingGeneration,
            ToolOwnerPackageId,
            ExecutionTargetOwnerPackageId,
            AuthorityActivationId,
            AuthorityUseCount,
            CanIssueOutsideAuthority);
}
public sealed record ToolReadinessRequest(string ToolId, AgentToolSourceContextWire Context);
public sealed record ToolInvocationRequest(AgentToolExecutionContextWire Context, AgentToolRequest Request);

public sealed record AgentPermissionRequestWire(AgentPermissionRequest Request, IReadOnlyList<string> ResourceCapabilities)
{
    public static AgentPermissionRequestWire FromRequest(AgentPermissionRequest request)
        => new(request, request.ResourceCapabilities);

    public AgentPermissionRequest ToRequest()
        => Request with { ResourceCapabilities = ResourceCapabilities };
}
