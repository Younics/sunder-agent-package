using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Memory.Semantic.PackageViews;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic;

public sealed class PackageModule : ISunderPackageModule
{
    public void ConfigureServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton(new MemoryLocalStore(context));
        services.AddSingleton(new MemorySemanticSettingsService(context));
        services.AddSingleton<SemanticMemoryMetricsService>();
        services.AddSingleton<SemanticEmbeddingContextResolver>();
        services.AddSingleton<ProfileConfiguredEmbeddingProviderResolver>();
        services.AddSingleton<SemanticMemoryRetrievalBackend>();
        services.AddSingleton<SemanticMemoryIndexingBackgroundService>();
        services.AddSingleton<SemanticMemoryRecallService>();
        services.AddSingleton<SemanticMemoryPromotionService>();
        services.AddSingleton<MemoryWorkingSummaryBuilder>();
        services.AddSingleton<MemoryInspectorService>();
        services.AddSingleton<MemorySemanticFeature>();
    }

    public void RegisterContributions(IPackageContributionRegistry registry, IServiceProvider services)
    {
        registry.RegisterConfigurationSchema(MemorySemanticPackageConfiguration.Schema);
        registry.RegisterBackgroundService<SemanticMemoryIndexingBackgroundService>();
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
