using System.Collections;
using System.Reflection;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifySubagentCatalogConsumers(Assembly worker)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var peers = AddCatalogPeers(state);
        var rpc = new MemoryConsumerRpc(new RegressionTestRpcClient(state, "sunder.package.agent.subagents"));
        var type = worker.GetType("Sunder.Package.Agent.Subagents.Rpc.SubagentRuntimeBindings", true)!;
        var instance = Activator.CreateInstance(type, rpc)!;
        await using var owned = (IAsyncDisposable)instance;
        Assert.Empty(References("GetChatProviders"));
        Assert.Empty(References("GetToolSources"));
        Assert.Empty(References("GetSelectableCapabilityProviders"));
        Assert.Equal(0, rpc.DiscoveryCount);
        await type.GetMethod("StartAsync")!.CreateDelegate<Func<CancellationToken, ValueTask>>(instance)(default);
        var chatReference = Assert.Single(References("GetChatProviders"));
        using var chat = AcquireSubagent(chatReference);
        Assert.Same(worker, chat.Service.Value.GetType().Assembly);
        Assert.Equal(peers.Chat.Descriptor.ProviderId, chat.Service.Property<AgentProviderDescriptor>("Descriptor").ProviderId);
        Func<CancellationToken, ValueTask<IReadOnlyList<AgentModelDescriptor>>> models = token => chat.Service.InvokeAsync<IReadOnlyList<AgentModelDescriptor>>("GetAvailableModelsAsync", token);
        Func<CancellationToken, ValueTask<AgentProviderReadiness>> readiness = token => chat.Service.InvokeAsync<AgentProviderReadiness>("GetReadinessAsync", token);
        var values = await models(default);
        Assert.Equal(2, values.Count);
        Assert.Equal(AgentReasoningEffort.ExtraHigh, values[1].Variants![0].ReasoningEffort);
        Assert.Equal(new DateOnly(2026, 9, 20), values[1].ReleaseDate);
        Assert.True(values[1].ModeOptions![0].DisablesReasoning);
        Assert.Equal(AgentProviderReadinessStatus.Ready, (await readiness(default)).Status);
        using var tools = AcquireSubagent(Assert.Single(References("GetToolSources")));
        Func<AgentToolSourceContext, CancellationToken, ValueTask<IReadOnlyList<AgentToolDescriptor>>> listTools = (context, token) => tools.Service.InvokeAsync<IReadOnlyList<AgentToolDescriptor>>("ListToolsAsync", context, token);
        Assert.Equal(3, (await listTools(new(null, null, null, null), default)).Count);
        Assert.True((await listTools(new(null, null, null, null), default))[0].IsReadOnly);
        peers.Tools.ReadOnly = false;
        Assert.False((await listTools(new(null, null, null, null), default))[0].IsReadOnly);
        var capabilityReference = Assert.Single(References("GetSelectableCapabilityProviders"));
        using var capabilities = AcquireSubagent(capabilityReference);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(worker.GetType("Sunder.Package.Agent.Contracts.Contracts.IAgentProfileSelectableCapabilityChangeNotifier", true)!.IsInstanceOfType(capabilities.Service.Value));
        capabilities.Service.On("SelectableCapabilitiesChanged", (Action)(() => changed.TrySetResult()));
        await peers.Capabilities.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, (await capabilities.Service.InvokeAsync<AgentProfileSelectableCapabilityDescriptor[]>("ListCapabilitiesAsync", new AgentProfileSelectableCapabilityRequest(null), CancellationToken.None)).Length);
        peers.Capabilities.Notify();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var calls = peers.Chat.ModelCalls;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => models(cancelled.Token).AsTask());
            Assert.Equal(calls, peers.Chat.ModelCalls);
        }
        rpc.LookupFailure = Sunder.Sdk.Rpc.SunderRpcErrorKind.PermissionDenied;
        Assert.Equal(Sunder.Sdk.Rpc.SunderRpcErrorKind.PermissionDenied,
            (await Assert.ThrowsAsync<Sunder.Sdk.Rpc.SunderRpcException>(() => models(default).AsTask())).Error.Kind);
        rpc.LookupFailure = null;
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
        var failure = await Record.ExceptionAsync(() => retiring.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(failure is OperationCanceledException || failure is Sunder.Sdk.Rpc.SunderRpcException { Error.Kind: Sunder.Sdk.Rpc.SunderRpcErrorKind.StaleEndpoint });
        Assert.Null(TryAcquireSubagent(chatReference));
        chat.Dispose();
        var replacement = new CatalogChat();
        AddGeneratedPeer(state, "example.catalog", AgentRpcContractIds.ChatProvider, replacement, "fixture.chat");
        Assert.Null(TryAcquireSubagent(chatReference));
        using (var current = AcquireSubagent(Assert.Single(References("GetChatProviders"))))
            Assert.NotNull(current.Service.Property<AgentProviderDescriptor>("Descriptor"));
        await state.RetireAsync(peers.Capabilities).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(TryAcquireSubagent(capabilityReference));
        capabilities.Dispose(); tools.Dispose();
        await owned.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, peers.Capabilities.Subscribers);
        Assert.Equal(0, rpc.ActiveWatches);
        foreach (var scope in rpc.Scopes)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => scope.DiscoverAsync(AgentRpcContractIds.ChatProvider).AsTask());
        object[] References(string name) => ((IEnumerable)type.GetMethod(name)!.Invoke(instance, null)!).Cast<object>().ToArray();
    }

    private static (CatalogChat Chat, CatalogTools Tools, CatalogCapabilities Capabilities) AddCatalogPeers(RegressionTestRpcState state)
    {
        var chat = new CatalogChat(); var tools = new CatalogTools(); var capabilities = new CatalogCapabilities();
        AddGeneratedPeer(state, "example.catalog", AgentRpcContractIds.ChatProvider, chat, "fixture.chat");
        AddGeneratedPeer(state, "example.catalog", AgentRpcContractIds.ToolSource, tools, "fixture.tools");
        AddGeneratedPeer(state, "example.catalog", AgentRpcContractIds.SelectableCapabilityProvider, capabilities, "fixture.capabilities");
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
