using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Memory.Semantic.PackageViews;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Package.Agent.Shared.Composition;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.Package.Agent.Memory.Semantic;

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
