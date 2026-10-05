extern alias LMStudioApp;

using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Provider.LMStudio.Rpc;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Package.Agent.TestSupport.Avalonia;
using Xunit;
using LMStudioSettingsView = LMStudioApp::Sunder.Package.Agent.Provider.LMStudio.LMStudioSettingsView;
using LMStudioSettingsViewModel = LMStudioApp::Sunder.Package.Agent.Provider.LMStudio.LMStudioSettingsViewModel;
using LMStudioCredentialOwnerRpcClient = LMStudioApp::Sunder.Package.Agent.Provider.LMStudio.ProviderCredentialOwnerRpcClient;

namespace Sunder.Package.Agent.Provider.LMStudio.Tests;

public sealed class LMStudioPackageCompositionTests
{
    [Fact]
    [Trait("Lane", "PackageCompatibility")]
    public void WorkerAndAppComposition_KeepConnectionCatalogAndProvidersWorkerOnly()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.lmstudio");
        var credentials = new ProviderCredentialAccessor(
            context.Secrets,
            LMStudioProviderConfiguration.ApiKeyKey);
        var connection = new LMStudioConnection(context, credentials);
        var catalog = new LMStudioModelCatalogService(connection);

        Assert.IsAssignableFrom<Sunder.Sdk.Rpc.ISunderRpcServiceHandler>(
            new CredentialSettingsService(credentials).CreateSunderRpcProviderRegistration().Handler);
        Assert.IsAssignableFrom<IAgentChatProvider>(
            new LMStudioAgentProvider(context, connection, catalog));
        Assert.IsAssignableFrom<IAgentEmbeddingProvider>(
            new LMStudioEmbeddingProvider(context, connection, catalog));
        Assert.NotNull(LMStudioProviderConfiguration.Schema);

        var appServices = new ServiceCollection();
        var appModule = GeneratedProviderComposition.App(typeof(LMStudioSettingsViewModel).Assembly);
        appModule.ConfigureAppServices(appServices, context);
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(LMStudioSettingsViewModel));
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(LMStudioCredentialOwnerRpcClient));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialAccessor));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(CredentialSettingsService));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(LMStudioConnection));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(LMStudioModelCatalogService));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(LMStudioAgentProvider));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(LMStudioEmbeddingProvider));

        using var appProvider = appServices.BuildServiceProvider();
        var appRegistry = new RecordingPackageContributionRegistry();
        appModule.RegisterAppContributions(appRegistry, appProvider);
        Assert.Equal([typeof(LMStudioSettingsView)], appRegistry.SettingsViewTypes);
        Assert.Empty(appRegistry.PackageViews);
        Assert.Throws<InvalidOperationException>(() => appRegistry.RegisterPackageView<LMStudioSettingsView>(
            new("forbidden.lmstudio", "Forbidden provider package view")));
    }

}
