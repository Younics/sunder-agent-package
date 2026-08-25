extern alias AnthropicApp;

using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Provider.Anthropic.Protocol.Generated;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Package.Agent.TestSupport.Avalonia;
using Xunit;
using AnthropicAppPackageModule = AnthropicApp::Sunder.Package.Agent.Provider.Anthropic.AppPackageModule;
using AnthropicSettingsView = AnthropicApp::Sunder.Package.Agent.Provider.Anthropic.AnthropicSettingsView;
using AnthropicSettingsViewModel = AnthropicApp::Sunder.Package.Agent.Provider.Anthropic.AnthropicSettingsViewModel;
using AnthropicCredentialOwnerRpcClient = AnthropicApp::Sunder.Package.Agent.Provider.Anthropic.ProviderCredentialOwnerRpcClient;

namespace Sunder.Package.Agent.Provider.Anthropic.Tests;

public sealed class AnthropicPackageCompositionTests
{
    [Fact]
    [Trait("Lane", "PackageCompatibility")]
    public void WorkerAndAppComposition_KeepProviderExecutionWorkerOnly()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.anthropic");
        var credentials = new ProviderCredentialAccessor(
            context.Secrets,
            AnthropicProviderConfiguration.ApiKeySecretKey);

        Assert.IsAssignableFrom<ICredentialSettingsProvider>(
            new ProviderCredentialRpcProvider(credentials));
        Assert.IsAssignableFrom<IAgentChatProvider>(
            new AnthropicAgentProvider(context, credentials));
        Assert.NotNull(AnthropicProviderConfiguration.Schema);

        var appServices = new ServiceCollection();
        var appModule = new AnthropicAppPackageModule();
        appModule.ConfigureAppServices(appServices, context);
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(AnthropicSettingsViewModel));
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(AnthropicCredentialOwnerRpcClient));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialAccessor));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialRpcProvider));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(AnthropicAgentProvider));

        using var appProvider = appServices.BuildServiceProvider();
        var appRegistry = new RecordingPackageContributionRegistry();
        appModule.RegisterAppContributions(appRegistry, appProvider);
        Assert.Equal([typeof(AnthropicSettingsView)], appRegistry.SettingsViewTypes);
        Assert.Empty(appRegistry.PackageViews);
        Assert.Throws<InvalidOperationException>(() => appRegistry.RegisterPackageView<AnthropicSettingsView>(
            new("forbidden.anthropic", "Forbidden provider package view")));
    }

}
