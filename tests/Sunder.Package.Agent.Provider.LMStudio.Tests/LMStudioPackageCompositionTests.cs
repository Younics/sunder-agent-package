using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Provider.LMStudio.Tests;

public sealed class LMStudioPackageCompositionTests
{
    [Fact]
    public void RuntimeAndAppComposition_KeepConnectionCatalogAndProvidersRuntimeOnly()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.lmstudio");
        var runtimeServices = new ServiceCollection();
        var runtimeModule = new PackageModule();
        runtimeModule.ConfigureRuntimeServices(runtimeServices, context);

        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialAccessor));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(LMStudioConnection));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(LMStudioModelCatalogService));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(LMStudioAgentProvider));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(LMStudioEmbeddingProvider));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(IAgentChatProvider));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(IAgentEmbeddingProvider));
        Assert.DoesNotContain(runtimeServices, descriptor => descriptor.ServiceType == typeof(LMStudioSettingsViewModel));

        using var runtimeProvider = runtimeServices.BuildServiceProvider();
        var runtimeRegistry = new ProviderCompositionTestRegistry();
        runtimeModule.RegisterRuntimeContributions(runtimeRegistry, runtimeProvider);
        Assert.Equal(
            [PackageExtensionPoints.ChatProviders.Id, PackageExtensionPoints.EmbeddingProviders.Id],
            runtimeRegistry.ExtensionIds);
        Assert.Equal([typeof(LMStudioAgentProvider), typeof(LMStudioEmbeddingProvider)], runtimeRegistry.ExtensionTypes);
        Assert.Equal([context.PackageId], runtimeRegistry.ConfigurationPackageIds);

        var appServices = new ServiceCollection();
        var appModule = new AppPackageModule();
        appModule.ConfigureAppServices(appServices, context);
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(LMStudioSettingsViewModel));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialAccessor));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(LMStudioConnection));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(LMStudioModelCatalogService));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(LMStudioAgentProvider));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(LMStudioEmbeddingProvider));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(IAgentChatProvider));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(IAgentEmbeddingProvider));

        using var appProvider = appServices.BuildServiceProvider();
        var appRegistry = new ProviderCompositionTestRegistry();
        appModule.RegisterAppContributions(appRegistry, appProvider);
        Assert.Equal([typeof(LMStudioSettingsView)], appRegistry.SettingsViewTypes);
        Assert.Empty(appRegistry.ExtensionIds);
    }
}
