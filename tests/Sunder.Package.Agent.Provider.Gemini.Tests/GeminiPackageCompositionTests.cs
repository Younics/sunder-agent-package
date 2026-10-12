extern alias GeminiApp;

using Microsoft.Extensions.DependencyInjection;
using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Sunder.Package.Agent.Provider.Gemini.Rpc;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Package.Agent.TestSupport.Avalonia;
using Xunit;
using GeminiSettingsView = GeminiApp::Sunder.Package.Agent.Provider.Gemini.GeminiSettingsView;
using GeminiSettingsViewModel = GeminiApp::Sunder.Package.Agent.Provider.Gemini.GeminiSettingsViewModel;
using ICredentialSettingsService = GeminiApp::Sunder.Generated.Packages.Sunder_Package_Agent_Provider_Gemini_Services.ICredentialSettingsService;

namespace Sunder.Package.Agent.Provider.Gemini.Tests;

public sealed class GeminiPackageCompositionTests
{
    [Fact]
    [Trait("Lane", "PackageCompatibility")]
    public void WorkerAndAppComposition_KeepProvidersAndEmbeddingsWorkerOnly()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.gemini");
        var credentials = new ProviderCredentialAccessor(
            context.Secrets,
            GeminiProviderConfiguration.ApiKeySecretKey);

        Assert.IsAssignableFrom<Sunder.Sdk.Rpc.ISunderRpcServiceHandler>(
            new CredentialSettingsService(credentials).CreateSunderRpcProviderRegistration().Handler);
        var chat = new GeminiAgentProvider(context, credentials);
        Assert.IsAssignableFrom<IChatProviderService>(chat);
        Assert.Equal("gemini.chat", chat.CreateSunderRpcProviderRegistration().ProviderId);
        var embeddings = new GeminiEmbeddingProvider(context, credentials);
        Assert.IsAssignableFrom<IEmbeddingProviderService>(embeddings);
        var embeddingRegistration = embeddings.CreateSunderRpcProviderRegistration();
        Assert.Equal("gemini.embedding", embeddingRegistration.ProviderId);
        Assert.IsAssignableFrom<Sunder.Sdk.Rpc.ISunderRpcServiceHandler>(embeddingRegistration.Handler);
        Assert.NotNull(GeminiProviderConfiguration.Schema);

        var appServices = new ServiceCollection();
        var appModule = GeneratedProviderComposition.App(typeof(GeminiSettingsViewModel).Assembly);
        appModule.ConfigureAppServices(appServices, context);
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(GeminiSettingsViewModel));
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(ICredentialSettingsService));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialAccessor));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(CredentialSettingsService));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(GeminiAgentProvider));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(GeminiEmbeddingProvider));

        using var appProvider = appServices.BuildServiceProvider();
        var appRegistry = new RecordingPackageContributionRegistry();
        appModule.RegisterAppContributions(appRegistry, appProvider);
        Assert.Equal([typeof(GeminiSettingsView)], appRegistry.SettingsViewTypes);
        Assert.Empty(appRegistry.PackageViews);
        Assert.Throws<InvalidOperationException>(() => appRegistry.RegisterPackageView<GeminiSettingsView>(
            new("forbidden.gemini", "Forbidden provider package view")));
    }

}
