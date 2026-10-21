using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal partial class RegressionTestExtensionCatalog : IDisposable, IAsyncDisposable
{
    private readonly RegressionTestRpcClient _client;
    private readonly RegressionTestSourceProviders _providers = new();
    private int _catalogDisposed;
    private Task? _disposeTask;
    private readonly object _consumerGate = new();
    private readonly Dictionary<(Type Type, string Caller), IAsyncDisposable> _consumers = [];
    private readonly Dictionary<string, GeneratedServiceTestScope> _receiverScopes = new(StringComparer.Ordinal);

    public RegressionTestExtensionCatalog(bool workerStyleProviderBoundary = false)
        : this(new RegressionTestRpcClient(
            new RegressionTestRpcState(workerStyleProviderBoundary),
            "younics.ai.agent"))
    {
    }

    internal RegressionTestExtensionCatalog(RegressionTestRpcClient client)
    {
        _client = client;
        ActiveRuns = new AgentActiveRunRegistry();
        RunControlProvider = new AgentRunControlService(ActiveRuns);
        AddGeneratedProvider("younics.ai.agent", RunControlProvider,
            Receiver("younics.ai.agent").Register(RunControlProvider, "younics.ai.agent.run.control"));
    }

    internal AgentActiveRunRegistry ActiveRuns { get; }
    internal AgentRunControlService RunControlProvider { get; }
    internal IServiceProviders<AgentServices.IBehaviorLoopService> BehaviorLoops => GetServiceProviders<AgentServices.IBehaviorLoopService>();
    internal IServiceProviders<AgentServices.IRunControlService> RunControls => GetServiceProviders<AgentServices.IRunControlService>();
    internal IServiceProviders<AgentServices.IChatProviderService> ChatProviders => GetServiceProviders<AgentServices.IChatProviderService>();
    internal IServiceProviders<AgentServices.ISelectableCapabilityProviderService> SelectableCapabilityProviders => GetServiceProviders<AgentServices.ISelectableCapabilityProviderService>();
    internal IServiceProviders<AgentServices.ISessionCleanerService> SessionCleaners => GetServiceProviders<AgentServices.ISessionCleanerService>();

    internal AgentProfileService CreateProfileService(Sunder.Package.Agent.Storage.AgentLocalStore store, AgentToolService tools)
        => new(store, tools, ChatProviders, BehaviorLoops, EmbeddingProviders,
            ProfileCapabilityConsumers, SelectableCapabilityProviders, ToolSources, ExecutionTargets);

    internal IServiceProviders<AgentServices.IEmbeddingProviderService> EmbeddingProviders
        => GetServiceProviders<AgentServices.IEmbeddingProviderService>();

    internal IServiceProviders<AgentServices.ISystemPromptContributorService> SystemPromptContributors
        => GetServiceProviders<AgentServices.ISystemPromptContributorService>();

    internal IServiceProviders<AgentServices.IPermissionSurfaceService> PermissionSurfaces
        => GetServiceProviders<AgentServices.IPermissionSurfaceService>();

    internal IServiceProviders<AgentServices.IExecutionTargetService> ExecutionTargets
        => GetServiceProviders<AgentServices.IExecutionTargetService>();

    internal IServiceProviders<AgentServices.IToolSourceService> ToolSources
        => GetServiceProviders<AgentServices.IToolSourceService>();

    internal IServiceProviders<AgentServices.IToolExecutionObservedService> ObservedToolExecutions
        => GetServiceProviders<AgentServices.IToolExecutionObservedService>();

    internal IServiceProviders<AgentServices.IPromptContextContributorService> PromptContextContributors
        => GetServiceProviders<AgentServices.IPromptContextContributorService>();

    internal IServiceProviders<AgentServices.IDurableLifecycleObserverService> DurableLifecycleObservers
        => GetServiceProviders<AgentServices.IDurableLifecycleObserverService>();

    internal IServiceProviders<AgentServices.IProfileCapabilityConsumerService> ProfileCapabilityConsumers
        => GetServiceProviders<AgentServices.IProfileCapabilityConsumerService>();

    internal IServiceProviders<AgentServices.IRuntimeCatalogService> RuntimeCatalogs
        => GetServiceProviders<AgentServices.IRuntimeCatalogService>();

    internal IServiceProviders<AgentServices.IChildRunExecutorService> ChildRunExecutors
        => GetServiceProviders<AgentServices.IChildRunExecutorService>();

    internal IServiceProviders<AgentServices.IWorkspaceExecutionResolverService> WorkspaceExecutionResolvers
        => GetServiceProviders<AgentServices.IWorkspaceExecutionResolverService>();

    internal IServiceProviders<AgentServices.IWorkspaceEditorService> WorkerWorkspaceEditors
        => GetServiceProviders<AgentServices.IWorkspaceEditorService>();

    internal IServiceProviders<TService> GetServiceProviders<TService>() where TService : class
        => Receiver("younics.ai.agent").Providers<TService>();

    internal GeneratedServiceTestScope Receiver(string caller)
    {
        lock (_consumerGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _catalogDisposed) != 0, this);
            if (!_receiverScopes.TryGetValue(caller, out var scope))
                _receiverScopes.Add(caller, scope = new GeneratedServiceTestScope(_client.ForCaller(caller)));
            return scope;
        }
    }

    public int DiscoveryCount => _client.State.DiscoveryCount;

    public Exception? LastInvocationFailure => _client.State.LastInvocationFailure;

    public SunderRpcEndpointReference? LastInvariantViolationEndpoint
        => _client.State.LastInvariantViolationEndpoint;

    public string? LastInvariantViolationPackageId
        => _client.State.LastInvariantViolationPackageId;

    public int InvariantViolationReportCount => _client.State.InvariantViolationReportCount;

    internal ISunderRpcClient RpcForCaller(string packageId) => _client.ForCaller(packageId);

    internal T OwnConsumer<T>(string caller, Func<ISunderRpcClient, T> create) where T : class, IAsyncDisposable
    {
        lock (_consumerGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _catalogDisposed) != 0, this);
            var key = (typeof(T), caller);
            if (!_consumers.TryGetValue(key, out var value))
            {
                value = create(_client.ForCaller(caller));
                _consumers.Add(key, value);
            }
            return (T)value;
        }
    }

    public void SetProviderLookupFailure(string callerPackageId, SunderRpcErrorKind? errorKind)
        => _client.State.SetProviderLookupFailure(callerPackageId, errorKind);

    internal async ValueTask<ServiceProvider<TService>> GetRequiredProviderAsync<TService>(
        IServiceProviders<TService> providers, CancellationToken cancellationToken = default) where TService : class
        => Assert.Single((await providers.ListAsync(cancellationToken).ConfigureAwait(false)).Providers);

    internal async ValueTask<ServiceProvider<TService>> GetRequiredProviderAsync<TService>(
        IServiceProviders<TService> providers, Func<ServiceProvider<TService>, bool> predicate,
        CancellationToken cancellationToken = default) where TService : class
    {
        var catalog = await providers.ListAsync(cancellationToken).ConfigureAwait(false);
        var matches = catalog.Providers.Where(predicate).ToArray();
        return Assert.Single(matches);
    }

    public Task RetireProviderAsync(object implementation)
        => _client.State.RetireAsync(implementation);

    public Task RetireProviderAsync(string _, object implementation)
        => RetireProviderAsync(implementation);

    public void RemoveProvider(object implementation)
        => _ = RetireProviderAsync(implementation);

    public void RemoveProvider(string _, object implementation)
        => RemoveProvider(implementation);

    public void Dispose() => Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        IAsyncDisposable[] consumers;
        GeneratedServiceTestScope[] receivers;
        TaskCompletionSource completion;
        lock (_consumerGate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            Volatile.Write(ref _catalogDisposed, 1);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            consumers = _consumers.Values.ToArray(); _consumers.Clear();
            receivers = _receiverScopes.Values.ToArray(); _receiverScopes.Clear();
        }
        _ = DrainAsync(consumers, receivers, completion);
        return new(completion.Task);
    }

    private async Task DrainAsync(IAsyncDisposable[] consumers, GeneratedServiceTestScope[] receivers, TaskCompletionSource completion)
    {
        try
        {
            try
            {
                // Every owner is drained even if another owner's cleanup fails.
                // Consumers finish before their borrowed receivers are disposed.
                try
                {
                    await Task.WhenAll(consumers.Select(async consumer =>
                        await consumer.DisposeAsync().ConfigureAwait(false))).ConfigureAwait(false);
                }
                finally
                {
                    await Task.WhenAll(receivers.Select(async receiver =>
                        await receiver.DisposeAsync().ConfigureAwait(false))).ConfigureAwait(false);
                }
            }
            finally
            {
                try { _client.State.Dispose(); }
                finally { _providers.Dispose(); }
            }
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
