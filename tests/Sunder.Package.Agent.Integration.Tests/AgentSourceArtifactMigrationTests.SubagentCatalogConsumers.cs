using System.Collections;
using System.Reflection;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifySubagentCatalogConsumers(Assembly worker)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var peers = AddCatalogPeers(state);
        var scoped = new FilesRpcObserver(new RegressionTestRpcClient(state, "younics.ai.agent.subagents"));
        var rpc = new MemoryConsumerRpc(scoped);
        Assert.Null(worker.GetType("Sunder.Package.Agent.Subagents.Rpc.SubagentRuntimeBindings"));
        await using var owned = new GeneratedServiceTestScope(rpc);
        Assert.Equal(0, rpc.DiscoveryCount);
        var chatReference = Assert.Single(await ProjectedProviders(owned, worker, "IChatProviderService"));
        using var chat = chatReference.Service;
        Assert.Same(worker, chat.Value.GetType().Assembly);
        Assert.Equal(peers.Chat.Descriptor.ProviderId, (await chat.InvokeAsync<AgentProviderDescriptor>("DescribeAsync", CancellationToken.None)).ProviderId);
        Func<CancellationToken, ValueTask<IReadOnlyList<AgentModelDescriptor>>> models = token => chat.InvokeAsync<IReadOnlyList<AgentModelDescriptor>>("ListModelsAsync", token);
        Func<CancellationToken, ValueTask<AgentProviderReadiness>> readiness = token => chat.InvokeAsync<AgentProviderReadiness>("GetReadinessAsync", token);
        var values = await models(default);
        Assert.Equal(2, values.Count);
        Assert.Equal(AgentReasoningEffort.ExtraHigh, values[1].Variants![0].ReasoningEffort);
        Assert.Equal(new DateOnly(2026, 9, 20), values[1].ReleaseDate);
        Assert.True(values[1].ModeOptions![0].DisablesReasoning);
        Assert.Equal(AgentProviderReadinessStatus.Ready, (await readiness(default)).Status);
        using var tools = Assert.Single(await ProjectedProviders(owned, worker, "IToolSourceService")).Service;
        var contextType = tools.Value.GetType().GetMethod("ListToolsAsync")!.GetParameters()[0].ParameterType;
        var contextFree = Activator.CreateInstance(contextType)!;
        foreach (var member in new[] { "SessionId", "Profile", "Workspace", "ExecutionBinding", "ExecutionTarget", "ExecutionTargetConfigurationGeneration" })
            contextType.GetProperty(member)!.SetValue(contextFree, null);
        ValueTask<IReadOnlyList<AgentToolDescriptor>> ListTools() => tools.InvokeAsync<IReadOnlyList<AgentToolDescriptor>>("ListToolsAsync", contextFree, CancellationToken.None);
        Assert.Equal(3, (await ListTools()).Count);
        Assert.True((await ListTools())[0].IsReadOnly);
        peers.Tools.ReadOnly = false;
        Assert.False((await ListTools())[0].IsReadOnly);
        var capabilityReference = Assert.Single(await ProjectedProviders(owned, worker, "ISelectableCapabilityProviderService"));
        using var capabilities = capabilityReference.Service;
        Assert.Same(worker, capabilities.Value.GetType().Assembly);
        using var watchCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var watch = ProjectedCatalogEvents(capabilities.Value, watchCancellation.Token).GetAsyncEnumerator();
        Task<bool>? pendingWatch = null;
        Task<bool> MoveNext() => pendingWatch = watch.MoveNextAsync().AsTask();
        try
        {
        var first = MoveNext();
        await peers.Capabilities.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        var revision = watch.Current.GetProperty("revision").GetInt64();
        Assert.Equal(2, (await capabilities.InvokeAsync<AgentProfileSelectableCapabilityDescriptor[]>("ListAsync", null, CancellationToken.None)).Length);
        var changed = MoveNext();
        peers.Capabilities.Notify();
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(watch.Current.GetProperty("revision").GetInt64() > revision);
        var nextEvent = MoveNext();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var calls = peers.Chat.ModelCalls;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => models(cancelled.Token).AsTask());
            Assert.Equal(calls, peers.Chat.ModelCalls);
        }
        scoped.LookupFailure = Sunder.Sdk.Rpc.SunderRpcErrorKind.PermissionDenied;
        Assert.Equal(Sunder.Sdk.Rpc.SunderRpcErrorKind.PermissionDenied,
            (await Assert.ThrowsAsync<Sunder.Sdk.Rpc.SunderRpcException>(() => models(default).AsTask())).Error.Kind);
        scoped.LookupFailure = null;
        peers.Chat.Block = true;
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = models(cancelled.Token).AsTask();
            await peers.Chat.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        peers.Chat.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retiring = models(default).AsTask();
        await peers.Chat.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(peers.Chat).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => retiring.WaitAsync(TimeSpan.FromSeconds(5)))).Error.Kind);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => chatReference.EnsureCurrentAsync())).Error.Kind);
        chat.Dispose();
        var replacement = new CatalogChat();
        AddGeneratedPeer(state, "example.catalog", RegressionTestBindings.ChatProviders, replacement, "fixture.chat");
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => chatReference.EnsureCurrentAsync())).Error.Kind);
        Assert.Equal(0, replacement.ModelCalls);
        using (var current = Assert.Single(await ProjectedProviders(owned, worker, "IChatProviderService")).Service)
            Assert.NotNull(await current.InvokeAsync<AgentProviderDescriptor>("DescribeAsync", CancellationToken.None));
        await state.RetireAsync(peers.Capabilities).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => capabilityReference.EnsureCurrentAsync())).Error.Kind);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => nextEvent.WaitAsync(TimeSpan.FromSeconds(5)))).Error.Kind);
        capabilities.Dispose(); tools.Dispose();
        await owned.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, peers.Capabilities.Subscribers);
        Assert.Equal(0, rpc.ActiveWatches);
        foreach (var scope in rpc.Scopes)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.DiscoverAsync(RegressionTestBindings.ChatProviders).AsTask());
        }
        finally
        {
            await watchCancellation.CancelAsync();
            if (pendingWatch is not null) await Record.ExceptionAsync(() => pendingWatch.WaitAsync(TimeSpan.FromSeconds(5)));
            await watch.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static (CatalogChat Chat, CatalogTools Tools, CatalogCapabilities Capabilities) AddCatalogPeers(RegressionTestRpcState state)
    {
        var chat = new CatalogChat(); var tools = new CatalogTools(); var capabilities = new CatalogCapabilities();
        AddGeneratedPeer(state, "example.catalog", RegressionTestBindings.ChatProviders, chat, "fixture.chat");
        AddGeneratedPeer(state, "example.catalog", RegressionTestBindings.ToolSources, tools, "fixture.tools");
        AddGeneratedPeer(state, "example.catalog", RegressionTestBindings.SelectableCapabilityProviders, capabilities, "fixture.capabilities");
        return (chat, tools, capabilities);
    }
    private sealed class CatalogChat : IAgentChatProvider
    {
        internal int ModelCalls;
        internal bool Block;
        internal TaskCompletionSource Started { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentProviderDescriptor Descriptor { get; } = new("fixture.chat", "Fixture Chat", [], true, true);
        public async ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref ModelCalls);
            if (Block) { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            return [new("fixture/old", "Older", 8000, 2000) { ReleaseDate = new(2025, 1, 1) },
                new("fixture/new", "Newer", 32000, 4000, true, [new("deep", "Deep", null, AgentReasoningEffort.ExtraHigh)], [new("fast", "Fast")], [new("brief", "Brief", null, true)]) { ReleaseDate = new(2026, 9, 20) }];
        }
        public ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentProviderReadiness(Descriptor.ProviderId, AgentProviderReadinessStatus.Ready, "Ready"));
        public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Catalog consumers cannot request execution capabilities.");
        public ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Catalog consumers cannot create chat clients.");
    }
    private sealed class CatalogTools : IAgentToolSource
    {
        internal bool ReadOnly = true;
        public string SourceId => "fixture.tools";
        public string DisplayName => "Fixture Tools";
        public string SourceKind => "fixture";
        public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tool = new AgentToolDescriptor("fixture.read", "Read", "Read a fixture", ReadOnly, SourceId: SourceId, SourceKind: SourceKind, Aliases: ["fixture.alias"]);
            return ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>([tool, tool, tool with { ToolId = "fixture.group", SelectionScope = AgentToolSelectionScope.Group, SelectionGroupId = "group" }]);
        }
        public ValueTask<AgentToolReadiness?> GetReadinessAsync(string toolId, AgentToolSourceContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Catalog consumers cannot probe tool execution readiness.");
        public ValueTask<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Catalog consumers cannot execute tools.");
    }
    private sealed class CatalogCapabilities : IAgentProfileSelectableCapabilityProvider, IAgentProfileSelectableCapabilityChangeNotifier
    {
        private Action? _changed;
        internal int Subscribers;
        internal TaskCompletionSource Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ProviderId => "fixture.tools";
        public string DisplayName => "Fixture Capabilities";
        public event Action? SelectableCapabilitiesChanged
        {
            add { _changed += value; Interlocked.Increment(ref Subscribers); Subscribed.TrySetResult(); }
            remove { _changed -= value; Interlocked.Decrement(ref Subscribers); }
        }
        internal void Notify() => _changed?.Invoke();
        public ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(AgentProfileSelectableCapabilityRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = new AgentProfileSelectableCapabilityDescriptor("tool", "fixture.read", ProviderId, "Read", "Read a fixture");
            return ValueTask.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([descriptor, descriptor]);
        }
    }
}
