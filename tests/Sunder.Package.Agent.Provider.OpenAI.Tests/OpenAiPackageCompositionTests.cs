using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using Sunder.Package.Agent.Provider.OpenAI.Worker;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Package.Agent.TestSupport.Avalonia;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class OpenAiPackageCompositionTests
{
    [Fact]
    [Trait("Lane", "PackageCompatibility")]
    public async Task WorkerAndAppComposition_KeepProvidersCredentialsAndAuthWorkerOnly()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.openai");
        var options = OpenAiWorkerConfiguration.CreateCore(context);
        await using var composition = Assert.IsType<Sunder.Sdk.Worker.SunderWorkerComposition>(options.Composition);
        var workerServices = composition.Services;

        Assert.NotNull(workerServices.GetService<ProviderCredentialAccessor>());
        Assert.NotNull(workerServices.GetService<ApiKeyAuthStrategy>());
        Assert.NotNull(workerServices.GetService<CodexConnectedAuthStrategy>());
        Assert.NotNull(workerServices.GetService<OpenAiPackageAuthHandler>());
        Assert.NotNull(workerServices.GetService<OpenAiAuthOperationHandler>());
        Assert.NotNull(workerServices.GetService<OpenAiManagementRpcProvider>());
        Assert.NotNull(workerServices.GetService<OpenAiAgentProvider>());
        Assert.NotNull(workerServices.GetService<OpenAiEmbeddingProvider>());
        Assert.Null(workerServices.GetService<OpenAiSettingsViewModel>());
        Assert.Null(workerServices.GetService<OpenAiAuthPresentationService>());

        Assert.Equal(
            [
                "openai.chat",
                "openai.embedding",
                "openai.continuation.cleaner",
                OpenAiManagementProtocol.ProviderId,
            ],
            options.Providers.Select(provider => provider.ProviderId));
        Assert.All(options.Providers, provider => Assert.IsAssignableFrom<ISunderRpcServiceHandler>(provider.Handler));
        Assert.Same(OpenAiProviderConfiguration.Schema, options.SettingsSchema);
        Assert.Same(workerServices.GetRequiredService<OpenAiPackageAuthHandler>(), options.AuthHandler);
        Assert.Empty(options.CallbackHandlers);

        var appServices = new ServiceCollection();
        var appModule = new AppPackageModule();
        appModule.ConfigureAppServices(appServices, context);
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(IOpenAiOwnerManagementClient));
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(OpenAiSettingsViewModel));
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(OpenAiAuthPresentationService));
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialOwnerClient));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialAccessor));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(ApiKeyAuthStrategy));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(CodexConnectedAuthStrategy));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(OpenAiPackageAuthHandler));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(OpenAiManagementRpcProvider));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(OpenAiAgentProvider));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(OpenAiEmbeddingProvider));

        using var appProvider = appServices.BuildServiceProvider();
        var appRegistry = new RecordingPackageContributionRegistry();
        appModule.RegisterAppContributions(appRegistry, appProvider);
        Assert.Equal([typeof(OpenAiSettingsView)], appRegistry.SettingsViewTypes);
        Assert.Empty(appRegistry.PackageViews);
        Assert.Throws<InvalidOperationException>(() => appRegistry.RegisterPackageView<OpenAiSettingsView>(
            new("forbidden.openai", "Forbidden provider package view")));
    }

    [Fact]
    public async Task AuthStatusOperation_UsesRuntimeOwnedAuthState()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.openai");
        using var strategy = new CodexConnectedAuthStrategy(context);
        var handler = new OpenAiAuthOperationHandler(strategy);

        var result = await handler.GetStatusAsync();

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

        var result = await handler.GetStatusAsync();

        Assert.True(result.IsConnected);
        Assert.True(result.HasCachedSession);
        Assert.Equal(persistedSession.ExpiresAtUtc, result.ExpiresAtUtc);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task CodexSessionStore_WritesVersionedDocumentAndRoundTrips()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.openai");
        var session = new OpenAiCodexSession(
            "access-token",
            "refresh-token",
            DateTimeOffset.UtcNow.AddHours(1),
            "account-id");
        var store = new CodexSessionStore(context.Secrets);

        await store.SaveAsync(session);

        var payload = await context.Secrets.GetSecretAsync("auth.codex.session");
        using var document = JsonDocument.Parse(payload!);
        Assert.Equal(CodexSessionStore.DocumentSchemaId, document.RootElement.GetProperty("SchemaId").GetString());
        Assert.Equal(CodexSessionStore.DocumentSchemaVersion, document.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(session, await store.GetAsync());
    }

    [Fact]
    public async Task CodexSessionStore_ReadsReleasedUnversionedDocument()
    {
        var session = new OpenAiCodexSession(
            "legacy-access-token",
            "legacy-refresh-token",
            DateTimeOffset.UtcNow.AddHours(1),
            "legacy-account-id");
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.openai",
            secrets: new ProviderTestSecrets(new Dictionary<string, string>
            {
                ["auth.codex.session"] = JsonSerializer.Serialize(session),
            }));

        Assert.Equal(session, await new CodexSessionStore(context.Secrets).GetAsync());
    }

    [Fact]
    public async Task CodexSessionStore_FailsClosedOnFutureDocumentWithoutDeletingIt()
    {
        var session = new OpenAiCodexSession(
            "future-access-token",
            "future-refresh-token",
            DateTimeOffset.UtcNow.AddHours(1),
            "future-account-id");
        var payload = JsonSerializer.Serialize(new
        {
            SchemaId = CodexSessionStore.DocumentSchemaId,
            SchemaVersion = CodexSessionStore.DocumentSchemaVersion + 1,
            Session = session,
        });
        var secrets = new ProviderTestSecrets(new Dictionary<string, string>
        {
            ["auth.codex.session"] = payload,
        });
        var store = new CodexSessionStore(secrets);

        Assert.Null(await store.GetAsync());
        Assert.Equal(payload, await secrets.GetSecretAsync("auth.codex.session"));
    }
}
