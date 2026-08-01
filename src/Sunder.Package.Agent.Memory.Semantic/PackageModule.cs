using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Memory.Semantic.PackageViews;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Shared.Composition;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Agent.Memory.Semantic;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new MemoryLocalStore(context));
        services.AddSingleton(new MemorySemanticSettingsService(context));
        services.TryAddSingleton<AgentRpcCatalog>();
        services.AddSingleton<SemanticMemoryMetricsService>();
        services.AddSingleton<SemanticModelRuntimeResolver>();
        services.AddSingleton<SemanticMemoryRetrievalBackend>();
        services.AddSingleton<SemanticMemoryIndexingBackgroundService>();
        services.AddSingleton<SemanticMemoryRecallService>();
        services.AddSingleton<SemanticMemoryPromotionService>();
        services.AddSingleton<MemoryInspectorService>();
        services.AddSingleton<MemoryRuntimeHandler>();
        services.AddSingleton<MemoryRuntimeChangeStream>();
        services.AddSingleton<MemorySemanticFeature>();
    }

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterSettingsSchema(MemorySemanticPackageConfiguration.Schema);
        registry.RegisterBackgroundService<SemanticMemoryIndexingBackgroundService>();
        var feature = services.GetRequiredService<MemorySemanticFeature>();
        registry.RegisterRpcProvider("semantic.memory.prompt.context", AgentPromptContextContributorRpc.CreateHandler(feature));
        registry.RegisterRpcProvider("semantic.memory.lifecycle", AgentDurableLifecycleObserverRpc.CreateHandler(feature));
        registry.RegisterRpcProvider("semantic.memory.profile.capabilities", AgentProfileCapabilityConsumerRpc.CreateHandler(feature));
        registry.RegisterRpcProvider("semantic.memory.session.cleaner", AgentSessionCleanerRpc.CreateHandler(feature));
        registry.RegisterRuntimeOperation(MemoryRuntimeOperations.Query, services.GetRequiredService<MemoryRuntimeHandler>());
        registry.RegisterRuntimeOperation(MemoryRuntimeOperations.Command, services.GetRequiredService<MemoryRuntimeHandler>());
        registry.RegisterRuntimeStream(MemoryRuntimeOperations.Changes, services.GetRequiredService<MemoryRuntimeChangeStream>());
    }
}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<MemoryAppRuntimeGateway>();
        services.AddSingletonAlias<IMemoryInspectorGateway, MemoryAppRuntimeGateway>();
        services.AddTransient(provider => new MemoryInspectorViewModel(
            provider.GetRequiredService<IMemoryInspectorGateway>()));
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
        => registry.RegisterPackageView<MemoryInspectorView>(new PackageViewRegistration(
            "sunder.package.agent.memory.semantic.inspector",
            "Memory Inspector",
            "assets/memory-icon.png",
            defaultPlacement: PackageViewPlacement.LeftBottom));
}
