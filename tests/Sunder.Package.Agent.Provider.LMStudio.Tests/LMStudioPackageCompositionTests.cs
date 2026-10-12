extern alias LMStudioApp;

using Microsoft.Extensions.DependencyInjection;
using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Sunder.Package.Agent.Provider.LMStudio.Rpc;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Package.Agent.TestSupport.Avalonia;
using Xunit;
using LMStudioSettingsView = LMStudioApp::Sunder.Package.Agent.Provider.LMStudio.LMStudioSettingsView;
using LMStudioSettingsViewModel = LMStudioApp::Sunder.Package.Agent.Provider.LMStudio.LMStudioSettingsViewModel;
using ICredentialSettingsService = LMStudioApp::Sunder.Generated.Packages.Sunder_Package_Agent_Provider_Lmstudio_Services.ICredentialSettingsService;

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
        using var chat = new LMStudioAgentProvider(context, connection, catalog);
        Assert.IsAssignableFrom<IChatProviderService>(chat);
        Assert.Equal("lmstudio.chat", chat.CreateSunderRpcProviderRegistration().ProviderId);
        var embeddings = new LMStudioEmbeddingProvider(context, connection, catalog);
        Assert.IsAssignableFrom<IEmbeddingProviderService>(embeddings);
        var embeddingRegistration = embeddings.CreateSunderRpcProviderRegistration();
        Assert.Equal("lmstudio.embedding", embeddingRegistration.ProviderId);
        Assert.IsAssignableFrom<Sunder.Sdk.Rpc.ISunderRpcServiceHandler>(embeddingRegistration.Handler);
        Assert.NotNull(LMStudioProviderConfiguration.Schema);

        var appServices = new ServiceCollection();
        var appModule = GeneratedProviderComposition.App(typeof(LMStudioSettingsViewModel).Assembly);
        appModule.ConfigureAppServices(appServices, context);
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(LMStudioSettingsViewModel));
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(ICredentialSettingsService));
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
