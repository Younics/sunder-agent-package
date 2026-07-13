using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Provider.Anthropic.Tests;

public sealed class AnthropicPackageCompositionTests
{
    [Fact]
    public void RuntimeAndAppComposition_KeepProviderExecutionRuntimeOnly()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.anthropic");
        var runtimeServices = new ServiceCollection();
        var runtimeModule = new PackageModule();
        runtimeModule.ConfigureRuntimeServices(runtimeServices, context);

        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialAccessor));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(AnthropicAgentProvider));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(IAgentChatProvider));
        Assert.DoesNotContain(runtimeServices, descriptor => descriptor.ServiceType == typeof(AnthropicSettingsViewModel));

        using var runtimeProvider = runtimeServices.BuildServiceProvider();
        var runtimeRegistry = new ProviderCompositionTestRegistry();
        runtimeModule.RegisterRuntimeContributions(runtimeRegistry, runtimeProvider);
        Assert.Equal([PackageExtensionPoints.ChatProviders.Id], runtimeRegistry.ExtensionIds);
        Assert.Equal([typeof(AnthropicAgentProvider)], runtimeRegistry.ExtensionTypes);
        Assert.Equal([context.PackageId], runtimeRegistry.ConfigurationPackageIds);

        var appServices = new ServiceCollection();
        var appModule = new AppPackageModule();
        appModule.ConfigureAppServices(appServices, context);
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(AnthropicSettingsViewModel));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialAccessor));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(AnthropicAgentProvider));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(IAgentChatProvider));

        using var appProvider = appServices.BuildServiceProvider();
        var appRegistry = new ProviderCompositionTestRegistry();
        appModule.RegisterAppContributions(appRegistry, appProvider);
        Assert.Equal([typeof(AnthropicSettingsView)], appRegistry.SettingsViewTypes);
        Assert.Empty(appRegistry.ExtensionIds);
    }
}
