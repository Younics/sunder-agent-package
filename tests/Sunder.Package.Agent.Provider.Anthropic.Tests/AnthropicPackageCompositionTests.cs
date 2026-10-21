extern alias AnthropicApp;

using Microsoft.Extensions.DependencyInjection;
using IChatProviderService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Chat_Provider.IChatProviderService;
using Sunder.Package.Agent.Provider.Anthropic.Rpc;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Package.Agent.TestSupport.Avalonia;
using Xunit;
using AnthropicSettingsView = AnthropicApp::Sunder.Package.Agent.Provider.Anthropic.AnthropicSettingsView;
using AnthropicSettingsViewModel = AnthropicApp::Sunder.Package.Agent.Provider.Anthropic.AnthropicSettingsViewModel;
using ICredentialSettingsService = AnthropicApp::Sunder.Generated.Packages.Younics_Ai_Agent_Provider_Anthropic_Services.ICredentialSettingsService;

namespace Sunder.Package.Agent.Provider.Anthropic.Tests;

public sealed class AnthropicPackageCompositionTests
{
    [Fact]
    [Trait("Lane", "PackageCompatibility")]
    public void WorkerAndAppComposition_KeepProviderExecutionWorkerOnly()
    {
        var context = new ProviderTestPackageContext("younics.ai.agent.provider.anthropic");
        var credentials = new ProviderCredentialAccessor(
            context.Secrets,
            AnthropicProviderConfiguration.ApiKeySecretKey);

        Assert.IsAssignableFrom<Sunder.Sdk.Rpc.ISunderRpcServiceHandler>(
            new CredentialSettingsService(credentials).CreateSunderRpcProviderRegistration().Handler);
        var chat = new AnthropicAgentProvider(context, credentials);
        Assert.IsAssignableFrom<IChatProviderService>(chat);
        Assert.Equal("anthropic.chat", chat.CreateSunderRpcProviderRegistration().ProviderId);
        Assert.NotNull(AnthropicProviderConfiguration.Schema);

        var appServices = new ServiceCollection();
        var appModule = GeneratedProviderComposition.App(typeof(AnthropicSettingsViewModel).Assembly);
        appModule.ConfigureAppServices(appServices, context);
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(AnthropicSettingsViewModel));
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(ICredentialSettingsService));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialAccessor));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(CredentialSettingsService));
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
