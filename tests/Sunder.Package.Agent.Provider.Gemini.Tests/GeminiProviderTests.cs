using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Gemini;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Sdk.Runtime;
using Xunit;

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
        var credentialHandler = new ProviderCredentialRuntimeHandler(credentials);
        var runtime = new ProviderTestRuntimeClient(async (operationId, request, cancellationToken) =>
        {
            if (operationId == ProviderCredentialRuntimeOperations.Query.OperationId)
            {
                return await credentialHandler.HandleAsync((ProviderCredentialQuery)request, cancellationToken);
            }

            if (operationId == ProviderCredentialRuntimeOperations.Command.OperationId)
            {
                return await credentialHandler.HandleAsync((ProviderCredentialCommand)request, cancellationToken);
            }

            throw new InvalidOperationException($"Unexpected Runtime operation '{operationId}'.");
        });
        using var settings = new GeminiSettingsViewModel(context, runtime);
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
        using var settings = new GeminiSettingsViewModel(context, NullPackageRuntimeClient.Instance);

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

}
