using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class OpenAiPackageCompositionTests
{
    [Fact]
    public void RuntimeAndAppComposition_KeepProvidersCredentialsAndAuthRuntimeOnly()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.openai");
        var runtimeServices = new ServiceCollection();
        runtimeServices.AddSingleton<IPackageContext>(context);
        var runtimeModule = new PackageModule();
        runtimeModule.ConfigureRuntimeServices(runtimeServices, context);

        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialAccessor));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(ApiKeyAuthStrategy));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(CodexConnectedAuthStrategy));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(OpenAiPackageAuthHandler));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(IPackageAuthHandler));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(IPackageCallbackHandler));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(OpenAiAuthOperationHandler));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(OpenAiAgentProvider));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(OpenAiEmbeddingProvider));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(IAgentChatProvider));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(IAgentEmbeddingProvider));
        Assert.DoesNotContain(runtimeServices, descriptor => descriptor.ServiceType == typeof(OpenAiSettingsViewModel));
        Assert.DoesNotContain(runtimeServices, descriptor => descriptor.ServiceType == typeof(OpenAiAuthPresentationService));

        using var runtimeProvider = runtimeServices.BuildServiceProvider();
        var runtimeRegistry = new ProviderCompositionTestRegistry();
        runtimeModule.RegisterRuntimeContributions(runtimeRegistry, runtimeProvider);
        Assert.Equal(
            [
                PackageExtensionPoints.ChatProviders.Id,
                PackageExtensionPoints.EmbeddingProviders.Id,
                PackageExtensionPoints.SessionDataCleaners.Id,
            ],
            runtimeRegistry.ExtensionIds);
        Assert.Equal(
            [typeof(OpenAiAgentProvider), typeof(OpenAiEmbeddingProvider), typeof(CodexResponseContinuationStore)],
            runtimeRegistry.ExtensionTypes);
        Assert.Equal([context.PackageId], runtimeRegistry.ConfigurationPackageIds);
        Assert.Equal([OpenAiRuntimeOperations.Auth.OperationId], runtimeRegistry.RuntimeOperationIds);
        Assert.Equal([typeof(OpenAiAuthOperationHandler)], runtimeRegistry.RuntimeOperationHandlerTypes);

        var appServices = new ServiceCollection();
        appServices.AddSingleton<IPackageRuntimeClient>(NullPackageRuntimeClient.Instance);
        var appModule = new AppPackageModule();
        appModule.ConfigureAppServices(appServices, context);
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(OpenAiSettingsViewModel));
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(OpenAiAuthPresentationService));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialAccessor));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(ApiKeyAuthStrategy));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(CodexConnectedAuthStrategy));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(OpenAiPackageAuthHandler));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(IPackageAuthHandler));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(IPackageCallbackHandler));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(OpenAiAgentProvider));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(OpenAiEmbeddingProvider));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(IAgentChatProvider));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(IAgentEmbeddingProvider));

        using var appProvider = appServices.BuildServiceProvider();
        var appRegistry = new ProviderCompositionTestRegistry();
        appModule.RegisterAppContributions(appRegistry, appProvider);
        Assert.Equal([typeof(OpenAiSettingsView)], appRegistry.SettingsViewTypes);
        Assert.Empty(appRegistry.ExtensionIds);
        Assert.Empty(appRegistry.RuntimeOperationIds);
    }

    [Fact]
    public async Task AuthStatusOperation_UsesRuntimeOwnedAuthState()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.openai");
        using var strategy = new CodexConnectedAuthStrategy(context);
        var handler = new OpenAiAuthOperationHandler(strategy);

        var result = await handler.HandleAsync(
            new OpenAiAuthOperationRequest(OpenAiAuthOperationKind.GetStatus));

        Assert.False(result.IsConnected);
        Assert.False(result.HasCachedSession);
        Assert.Null(result.ExpiresAtUtc);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task AuthStatusOperation_RestoresPersistedActiveSessionWithFreshStrategy()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.openai");
        var persistedSession = new OpenAiCodexSession(
            "persisted-access-token",
            "persisted-refresh-token",
            DateTimeOffset.UtcNow.AddHours(1),
            "persisted-account-id");
        await new CodexSessionStore(context.Secrets).SaveAsync(persistedSession);
        using var strategy = new CodexConnectedAuthStrategy(context);
        var handler = new OpenAiAuthOperationHandler(strategy);

        var result = await handler.HandleAsync(
            new OpenAiAuthOperationRequest(OpenAiAuthOperationKind.GetStatus));

        Assert.True(result.IsConnected);
        Assert.True(result.HasCachedSession);
        Assert.Equal(persistedSession.ExpiresAtUtc, result.ExpiresAtUtc);
        Assert.Null(result.ErrorMessage);
    }
}
