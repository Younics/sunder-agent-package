using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;

SunderWorkerComposition? ownedComposition = null;
try
{
    await SunderWorker.RunAsync(context =>
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(context);
        services.AddSingleton<ISunderRpcClient>(context.Rpc);
        services.AddSingleton(AgentRpcCatalog.CreateDormant(context.Rpc));
        new PackageModule().ConfigureRuntimeServices(services, context);
        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        var registrations = new List<SunderWorkerProviderRegistration>();
        var deferred = new List<DeferredAgentRpcServiceHandler>();
        Register(
            registrations,
            deferred,
            "agent.runtime.catalog",
            AgentRpcContractDescriptors.Get(AgentRpcContractIds.RuntimeCatalog),
            services => AgentRuntimeCatalogRpc.CreateHandler(
                services.GetRequiredService<AgentRuntimeCatalog>()));
        Register(
            registrations,
            deferred,
            "agent.workspace.execution.resolver",
            AgentRpcContractDescriptors.Get(AgentRpcContractIds.WorkspaceExecutionResolver),
            services => AgentWorkspaceExecutionResolverRpc.CreateHandler(
                services.GetRequiredService<AgentWorkspaceExecutionResolver>()));
        Register(
            registrations,
            deferred,
            "agent.child.runs",
            AgentRpcContractDescriptors.Get(AgentRpcContractIds.ChildRunExecutor),
            services => AgentChildRunExecutorRpc.CreateHandler(
                services.GetRequiredService<AgentRunCoordinator>()));
        Register(
            registrations,
            deferred,
            "agent.attachment.cleaner",
            AgentRpcContractDescriptors.Get(AgentRpcContractIds.SessionCleaner),
            services => AgentSessionCleanerRpc.CreateHandler(
                services.GetRequiredService<AgentAttachmentService>()));
        Register(
            registrations,
            deferred,
            "agent.history.cleaner",
            AgentRpcContractDescriptors.Get(AgentRpcContractIds.SessionCleaner),
            services => AgentSessionCleanerRpc.CreateHandler(
                services.GetRequiredService<Sunder.Package.Agent.HistorySearch.HistorySearchIndexingService>()));
        Register(
            registrations,
            deferred,
            AgentRunControlRpc.ProviderId,
            AgentRpcContractDescriptors.Get(AgentRpcContractIds.RunControl),
            services => AgentRunControlRpc.CreateHandler(
                services.GetRequiredService<AgentRunControlRegistry>()));
        Register(
            registrations,
            deferred,
            "agent.behavior.loop",
            AgentRpcContractDescriptors.Get(AgentRpcContractIds.BehaviorLoop),
            services => AgentBehaviorLoopRpc.CreateHandler(
                services.GetRequiredService<Sunder.Package.Agent.Services.BehaviorLoops.DefaultAgentBehaviorLoop>(),
                services.GetRequiredService<AgentRpcCatalog>()));
        Register(
            registrations,
            deferred,
            "agent.workspace.documentation",
            AgentRpcContractDescriptors.Get(AgentRpcContractIds.PromptContextContributor),
            services => AgentPromptContextContributorRpc.CreateHandler(
                services.GetRequiredService<WorkspaceDocumentationContextService>()));
        Register(
            registrations,
            deferred,
            "agent.stack.profiles",
            SunderStackContributorRpc.Descriptor,
            services => SunderStackContributorRpc.CreateHandler(
                services.GetRequiredService<AgentProfileStackContributor>()));
        Register(
            registrations,
            deferred,
            "agent.stack.workspaces",
            SunderStackContributorRpc.Descriptor,
            services => SunderStackContributorRpc.CreateHandler(
                services.GetRequiredService<AgentWorkspaceStackContributor>()));
        Register(
            registrations,
            deferred,
            AgentOwnerRuntimeRpc.ProviderId,
            AgentOwnerRuntimeRpc.Descriptor,
            services => AgentOwnerRuntimeRpc.CreateHandler(
                services.GetRequiredService<AgentOwnerRuntimeProvider>()));

        var lifecycle = new AgentWorkerLifecycle(serviceProvider, deferred);
        var composition = new SunderWorkerComposition(serviceProvider, [lifecycle]);
        ownedComposition = composition;
        return new SunderWorkerOptions(registrations)
        {
            Composition = composition,
        };
    });
}
finally
{
    if (ownedComposition is not null) await ownedComposition.DisposeAsync();
}

static void Register(
    ICollection<SunderWorkerProviderRegistration> registrations,
    ICollection<DeferredAgentRpcServiceHandler> deferred,
    string providerId,
    SunderRpcContractDescriptor descriptor,
    Func<IServiceProvider, ISunderRpcServiceHandler> createHandler)
{
    var handler = new DeferredAgentRpcServiceHandler(providerId, createHandler);
    deferred.Add(handler);
    registrations.Add(new SunderWorkerProviderRegistration(
        providerId,
        descriptor.ContractId,
        descriptor.Version,
        descriptor.Sha256,
        descriptor.AbiSha256 ?? throw new InvalidDataException(
            $"RPC contract '{descriptor.ContractId}' is missing its V2 ABI identity."),
        handler));
}
