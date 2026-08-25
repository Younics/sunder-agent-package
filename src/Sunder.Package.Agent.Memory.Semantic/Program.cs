using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Memory.Semantic;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;

ServiceProvider? ownedServices = null;
SunderWorkerComposition? ownedComposition = null;
AgentRpcCatalog? ownedCatalog = null;
try
{
    await SunderWorker.RunAsync(context =>
    {
        var catalog = AgentRpcCatalog.CreateDormant(context.Rpc);
        ownedCatalog = catalog;
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(context);
        services.AddSingleton(catalog);
        services.AddSingleton(_ => MemoryLocalStore.CreateDeferred(context));
        services.AddSingleton(new MemorySemanticSettingsService(context));
        services.AddSingleton<SemanticMemoryMetricsService>();
        services.AddSingleton<SemanticModelRuntimeResolver>();
        services.AddSingleton<SemanticMemoryRetrievalBackend>();
        services.AddSingleton<SemanticMemoryIndexingBackgroundService>();
        services.AddSingleton<SemanticMemoryRecallService>();
        services.AddSingleton<SemanticMemoryPromotionService>();
        services.AddSingleton<MemoryInspectorService>();
        services.AddSingleton<MemoryRuntimeHandler>();
        services.AddSingleton<MemorySemanticFeature>();
        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        ownedServices = serviceProvider;

        var feature = serviceProvider.GetRequiredService<MemorySemanticFeature>();
        var runtimeHandler = serviceProvider.GetRequiredService<MemoryRuntimeHandler>();
        var indexing = serviceProvider.GetRequiredService<SemanticMemoryIndexingBackgroundService>();
        var catalogLifecycle = new AgentRpcCatalogLifecycle(
            catalog,
            serviceProvider.GetRequiredService<MemoryLocalStore>());
        var composition = new SunderWorkerComposition(
            serviceProvider,
            [catalogLifecycle, indexing]);
        ownedComposition = composition;

        return new SunderWorkerOptions(
        [
            Register(
                "semantic.memory.prompt.context",
                AgentRpcContractDescriptors.Get(AgentRpcContractIds.PromptContextContributor),
                AgentPromptContextContributorRpc.CreateHandler(feature, catalog)),
            Register(
                "semantic.memory.lifecycle",
                AgentRpcContractDescriptors.Get(AgentRpcContractIds.DurableLifecycleObserver),
                AgentDurableLifecycleObserverRpc.CreateHandler(feature)),
            Register(
                "semantic.memory.profile.capabilities",
                AgentRpcContractDescriptors.Get(AgentRpcContractIds.ProfileCapabilityConsumer),
                AgentProfileCapabilityConsumerRpc.CreateHandler(feature)),
            Register(
                "semantic.memory.session.cleaner",
                AgentRpcContractDescriptors.Get(AgentRpcContractIds.SessionCleaner),
                AgentSessionCleanerRpc.CreateHandler(feature)),
            Register(
                MemorySemanticProtocol.ProviderId,
                MemorySemanticProtocol.Inspector,
                MemoryInspectorRpc.CreateHandler(runtimeHandler)),
        ])
        {
            SettingsSchema = MemorySemanticPackageConfiguration.Schema,
            Composition = composition,
        };
    });
}
finally
{
    if (ownedComposition is not null)
    {
        await ownedComposition.DisposeAsync();
    }
    else if (ownedServices is not null)
    {
        await ownedServices.DisposeAsync();
    }
    ownedCatalog?.Dispose();
}

static SunderWorkerProviderRegistration Register(
    string providerId,
    SunderRpcContractDescriptor descriptor,
    ISunderRpcServiceHandler handler)
    => new(
        providerId,
        descriptor.ContractId,
        descriptor.Version,
        descriptor.Sha256,
        descriptor.AbiSha256 ?? throw new InvalidOperationException(
            $"RPC contract '{descriptor.ContractId}' does not have a V2 ABI identity."),
        handler);

internal sealed class AgentRpcCatalogLifecycle(
    AgentRpcCatalog catalog,
    MemoryLocalStore store) : ISunderWorkerLifecycleService
{
    private readonly object _syncRoot = new();
    private int _started;

    public Task Completion => Task.CompletedTask;

    public ValueTask PrepareCandidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask CommitGenerationAsync(
        SunderWorkerGenerationContext generation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ActivateAsync(
        SunderWorkerContext context,
        CancellationToken cancellationToken = default)
        => await StartAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask ShutdownAsync(
        SunderWorkerShutdownContext context,
        CancellationToken cancellationToken = default)
        => await StopAsync(cancellationToken).ConfigureAwait(false);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        store.Initialize(cancellationToken);
        lock (_syncRoot)
        {
            if (_started == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                catalog.StartWatching();
                _started = 1;
            }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        catalog.Dispose();
        return Task.CompletedTask;
    }
}
