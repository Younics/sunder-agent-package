extern alias GeminiApp;

using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Gemini;
using Sunder.Package.Agent.Provider.Gemini.Protocol.Generated;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Xunit;
using GeminiSettingsViewModel = GeminiApp::Sunder.Package.Agent.Provider.Gemini.GeminiSettingsViewModel;
using IProviderCredentialOwnerClient = GeminiApp::Sunder.Package.Agent.Provider.Shared.IProviderCredentialOwnerClient;
using ProviderCredentialSnapshot = GeminiApp::Sunder.Package.Agent.Provider.Shared.ProviderCredentialSnapshot;

namespace Sunder.Package.Agent.Provider.Gemini.Tests;

public sealed class GeminiProviderTests
{
    [Fact]
    public async Task EmbeddingReadiness_UsesCanonicalApiKeySecret()
    {
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.gemini",
            secretValues: new Dictionary<string, string>
            {
                [GeminiProviderConfiguration.ApiKeySecretKey] = "gemini-test-key",
            });
        var provider = new GeminiEmbeddingProvider(context);

        var readiness = await provider.GetReadinessAsync();

        Assert.Equal(AgentProviderReadinessStatus.Ready, readiness.Status);
    }

    [Fact]
    public async Task EmbeddingReadiness_DoesNotTreatOldReadTypoAsConfigured()
    {
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.gemini",
            secretValues: new Dictionary<string, string>
            {
                ["api.key"] = "unused-key",
            });
        var provider = new GeminiEmbeddingProvider(context);

        var readiness = await provider.GetReadinessAsync();

        Assert.Equal(AgentProviderReadinessStatus.NeedsConfiguration, readiness.Status);
    }

    [Fact]
    public async Task ChatReadiness_UsesCanonicalApiKeySecret()
    {
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.gemini",
            secretValues: new Dictionary<string, string>
            {
                [GeminiProviderConfiguration.ApiKeySecretKey] = "gemini-test-key",
            });

        var readiness = await new GeminiAgentProvider(context).GetReadinessAsync();

        Assert.Equal(AgentProviderReadinessStatus.Ready, readiness.Status);
    }

    [Fact]
    public async Task SettingsChatAndEmbeddings_ObserveTheSameCredentialAccessor()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.gemini");
        var credentials = new ProviderCredentialAccessor(
            context.Secrets,
            GeminiProviderConfiguration.ApiKeySecretKey);
        using var settings = new GeminiSettingsViewModel(
            context,
            new CredentialOwnerClientFake(credentials));
        var chat = new GeminiAgentProvider(context, credentials);
        var embeddings = new GeminiEmbeddingProvider(context, credentials);

        settings.ApiKeySettings.EnteredCredential = "shared-key";
        await settings.ApiKeySettings.SaveCredentialCommand.ExecuteAsync(null);

        Assert.Equal(AgentProviderReadinessStatus.Ready, (await chat.GetReadinessAsync()).Status);
        Assert.Equal(AgentProviderReadinessStatus.Ready, (await embeddings.GetReadinessAsync()).Status);

        settings.ApiKeySettings.RequestClearCredentialCommand.Execute(null);
        await settings.ApiKeySettings.ClearCredentialCommand.ExecuteAsync(null);

        Assert.Equal(AgentProviderReadinessStatus.NeedsConfiguration, (await chat.GetReadinessAsync()).Status);
        Assert.Equal(AgentProviderReadinessStatus.NeedsConfiguration, (await embeddings.GetReadinessAsync()).Status);
    }

    [Fact]
    public async Task UnknownUtilityModel_FallsBackToTheCanonicalDefault()
    {
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.gemini",
            new Dictionary<string, string>
            {
                [GeminiProviderConfiguration.UtilityModelKey] = "gemini/unknown-model",
            });
        using var settings = new GeminiSettingsViewModel(
            context,
            new CredentialOwnerClientFake(new ProviderCredentialAccessor(
                context.Secrets,
                GeminiProviderConfiguration.ApiKeySecretKey)));

        Assert.Equal(
            GeminiProviderConfiguration.DefaultUtilityModelId,
            settings.UtilityModelSettings.SelectedUtilityModel?.ModelId);
        Assert.Equal(
            GeminiProviderConfiguration.DefaultUtilityModelId,
            await new GeminiAgentProvider(context).ResolveUtilityModelIdAsync());
    }

    [Fact]
    public async Task Catalog_ExposesReasoningOnlyForKnownThinkingFamilies()
    {
        var models = await new GeminiAgentProvider(
            new ProviderTestPackageContext("sunder.package.agent.provider.gemini"))
            .GetAvailableModelsAsync();

        Assert.NotEmpty(models.Single(model => model.ModelId == "gemini/gemini-2.5-pro").Variants ?? []);
        Assert.NotEmpty(models.Single(model => model.ModelId == "gemini/gemini-3-flash-preview").Variants ?? []);
        Assert.Null(models.Single(model => model.ModelId == "gemini/gemini-flash-latest").Variants);
        Assert.Null(models.Single(model => model.ModelId == "gemini/gemma-4-31b-it").Variants);
    }

    private sealed class CredentialOwnerClientFake(ProviderCredentialAccessor credentials)
        : IProviderCredentialOwnerClient
    {
        private readonly ProviderCredentialRpcProvider _provider = new(credentials);

        public async ValueTask<ProviderCredentialSnapshot> GetStatusAsync(
            CancellationToken cancellationToken = default)
            => ToSnapshot(await _provider.QueryAsync(
                null!,
                new CredentialQuery(),
                cancellationToken));

        public async ValueTask<ProviderCredentialSnapshot> SetAsync(
            string credential,
            CancellationToken cancellationToken = default)
            => ToSnapshot(await _provider.CommandAsync(
                null!,
                new CredentialCommand { Kind = 0, Credential = credential },
                cancellationToken));

        public async ValueTask<ProviderCredentialSnapshot> ClearAsync(
            CancellationToken cancellationToken = default)
            => ToSnapshot(await _provider.CommandAsync(
                null!,
                new CredentialCommand { Kind = 1 },
                cancellationToken));

        private static ProviderCredentialSnapshot ToSnapshot(CredentialSnapshot snapshot)
            => new(snapshot.HasStoredCredential);
    }

}
