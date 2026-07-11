using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Memory.Semantic.PackageViews;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Agent.Memory.Semantic;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new MemoryLocalStore(context));
        services.AddSingleton(new MemorySemanticSettingsService(context));
        services.AddSingleton<SemanticMemoryMetricsService>();
        services.AddSingleton<SemanticModelRuntimeResolver>();
        services.AddSingleton<SemanticMemoryRetrievalBackend>();
        services.AddSingleton<SemanticMemoryIndexingBackgroundService>();
        services.AddSingleton<SemanticMemoryRecallService>();
        services.AddSingleton<SemanticMemoryPromotionService>();
        services.AddSingleton<MemoryInspectorService>();
        services.AddSingleton<MemorySemanticFeature>();
        services.AddTransient<MemoryInspectorViewModel>();
    }

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
        => ConfigureRuntimeServices(services, context);

    public void RegisterRuntimeContributions(ISunderRuntimeContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(MemorySemanticPackageConfiguration.Schema);
        registry.RegisterBackgroundService<SemanticMemoryIndexingBackgroundService>();
        registry.RegisterExtension(PackageExtensionPoints.PromptContextContributors, services.GetRequiredService<MemorySemanticFeature>());
        registry.RegisterExtension(PackageExtensionPoints.LifecycleObservers, services.GetRequiredService<MemorySemanticFeature>());
        registry.RegisterExtension(PackageExtensionPoints.ProfileCapabilityConsumers, services.GetRequiredService<MemorySemanticFeature>());
        registry.RegisterExtension(PackageExtensionPoints.SessionDataCleaners, services.GetRequiredService<MemorySemanticFeature>());
    }

    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterExtension(PackageExtensionPoints.PromptContextContributors, services.GetRequiredService<MemorySemanticFeature>());
        registry.RegisterExtension(PackageExtensionPoints.LifecycleObservers, services.GetRequiredService<MemorySemanticFeature>());
        registry.RegisterExtension(PackageExtensionPoints.ProfileCapabilityConsumers, services.GetRequiredService<MemorySemanticFeature>());
        registry.RegisterExtension(PackageExtensionPoints.SessionDataCleaners, services.GetRequiredService<MemorySemanticFeature>());
        registry.RegisterPackageView<MemoryInspectorView>(new PackageViewRegistration(
            "sunder.package.agent.memory.semantic.inspector",
            "Memory Inspector",
            "assets/memory-icon.png",
            defaultPlacement: PackageViewPlacement.LeftBottom));
    }
}

public sealed class AppPackageModule : ISunderAppPackageModule
{
    private readonly PackageModule _module = new();

    public void ConfigureAppServices(IServiceCollection services, IPackageContext context) => _module.ConfigureAppServices(services, context);
    public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services) => _module.RegisterAppContributions(registry, services);
}
