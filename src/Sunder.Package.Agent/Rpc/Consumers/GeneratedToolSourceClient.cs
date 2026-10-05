using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Tool_Source;
using Wire = Sunder.Generated.Rpc.ToolSource;

namespace Sunder.Package.Agent.Runtime.Rpc;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed class GeneratedToolSourceClient(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
    : IAgentToolSource, IAgentPermissionAwareToolSource, IAgentToolExecutionPreflightSource, IAgentToolPresentationResolver, IDisposable, IAsyncDisposable
{
    private readonly GeneratedProviderCallLifetime _calls = new(rpc, provider, "agent.tool.provider-stale");
    private Wire.ToolSourceDescription? _description;

    internal async ValueTask<(string SourceId, string DisplayName, string SourceKind)> DescribeAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); _calls.ThrowIfStopped();
        var value = Volatile.Read(ref _description);
        if (value is null)
        {
            var fetched = await _calls.Invoke((scope, cancellation) => Client(scope).DescribeAsync(new(), cancellation), token).ConfigureAwait(false);
            value = Interlocked.CompareExchange(ref _description, fetched, null) ?? fetched;
        }
        return (value.SourceId, value.DisplayName, value.SourceKind);
    }
    public string SourceId => DescribeAsync().AsTask().GetAwaiter().GetResult().SourceId;
    public string DisplayName => DescribeAsync().AsTask().GetAwaiter().GetResult().DisplayName;
    public string SourceKind => DescribeAsync().AsTask().GetAwaiter().GetResult().SourceKind;

    public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken cancellationToken = default)
        => _calls.Invoke<IReadOnlyList<AgentToolDescriptor>>(async (scope, token) => AgentConsumerValues.Convert<AgentToolDescriptor[]>(
            await Client(scope).ListToolsAsync(AgentToolValues.SourceContext<Wire.AgentToolSourceContextWire>(context), token).ConfigureAwait(false)), cancellationToken);
    public ValueTask<AgentToolReadiness?> GetReadinessAsync(string toolId, AgentToolSourceContext context, CancellationToken cancellationToken = default)
        => _calls.Invoke(async (scope, token) =>
        {
            var value = await Client(scope).GetReadinessAsync(new() { ToolId = toolId, Context = AgentToolValues.SourceContext<Wire.AgentToolSourceContextWire>(context) }, token).ConfigureAwait(false);
            return value is null ? null : AgentConsumerValues.Convert<AgentToolReadiness>(value);
        }, cancellationToken);
    public ValueTask<AgentToolResult?> PreflightExecutionAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
        => _calls.Invoke(async (scope, token) =>
        {
            var value = await Client(scope).PreflightAsync(AgentToolValues.Invocation<Wire.ToolInvocationRequest>(context, request), token).ConfigureAwait(false);
            return value is null ? null : AgentConsumerValues.Convert<AgentToolResult>(value);
        }, cancellationToken);
    public ValueTask<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
        => _calls.Invoke(async (scope, token) => AgentConsumerValues.Convert<AgentToolResult>(
            await Client(scope).ExecuteAsync(AgentToolValues.Invocation<Wire.ToolInvocationRequest>(context, request), token).ConfigureAwait(false)),
            cancellationToken, AgentToolValues.ExecutionOptions());
    public ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
        => _calls.Invoke(async (scope, token) =>
        {
            var value = await Client(scope).PermissionAsync(AgentToolValues.Invocation<Wire.ToolInvocationRequest>(context, request), token).ConfigureAwait(false);
            return value is null ? null : AgentConsumerValues.Convert<AgentPermissionRequest>(value.Request) with { ResourceCapabilities = value.ResourceCapabilities.ToArray() };
        }, cancellationToken);
    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
        => _calls.Invoke(async (scope, token) =>
        {
            var value = await Client(scope).PresentationAsync(AgentConsumerValues.Convert<Wire.AgentToolPresentationRequest>(request), token).ConfigureAwait(false);
            return value is null ? null : AgentConsumerValues.Convert<AgentToolPresentation>(value);
        }, default).AsTask().GetAwaiter().GetResult();

    private Wire.ToolSourceClient Client(ISunderRpcCallScope scope)
        => new PackageApi(scope).ToolSource.CreateClient(scope, new Api.Provider(provider)).STool_2dsource;
    public void Dispose() => _calls.Dispose();
    public ValueTask DisposeAsync() => _calls.DisposeAsync();
}
