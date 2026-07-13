using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Provider.LMStudio.Tests;

public sealed class LMStudioConnectionTests
{
    [Theory]
    [InlineData("not-a-url")]
    [InlineData("file:///tmp/lmstudio")]
    [InlineData("http://localhost:1234/v1?tenant=a")]
    [InlineData("http://user:password@localhost:1234/v1")]
    public async Task InvalidUri_IsRejectedAtSettingsAndReadinessBoundaries(string invalidUrl)
    {
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = LMStudioProviderConfiguration.DefaultBaseUrl,
            });
        var viewModel = new LMStudioSettingsViewModel(context)
        {
            BaseUrl = invalidUrl,
        };

        await viewModel.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal("Invalid", viewModel.ConnectionStatusLabel);
        Assert.Equal(
            LMStudioProviderConfiguration.DefaultBaseUrl,
            await context.Settings.GetStoredValueAsync(LMStudioProviderConfiguration.BaseUrlKey));

        await context.Settings.SetValueAsync(LMStudioProviderConfiguration.BaseUrlKey, invalidUrl);
        using var provider = new LMStudioAgentProvider(context);
        var readiness = await provider.GetReadinessAsync();

        Assert.Equal(AgentProviderReadinessStatus.NeedsConfiguration, readiness.Status);
        Assert.Contains("invalid", readiness.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EndpointAndCredentialRotation_UsesCurrentSnapshotWithOneHandler()
    {
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = "https://first.test/v1",
            },
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.ApiKeyKey] = "first-key",
            });
        var handler = new LMStudioTestHttpHandler((_, _, _) => LMStudioTestHttpHandler.Json("{\"data\":[]}"));
        using var connection = new LMStudioConnection(context, handler);
        var catalog = new LMStudioModelCatalogService(connection);

        Assert.True((await catalog.GetCatalogAsync()).IsSuccess);
        await context.Settings.SetValueAsync(LMStudioProviderConfiguration.BaseUrlKey, "https://second.test/api/v1/");
        await context.Secrets.SetSecretAsync(LMStudioProviderConfiguration.ApiKeyKey, "second-key");
        Assert.True((await catalog.GetCatalogAsync()).IsSuccess);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://first.test/v1/models", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("first-key", handler.Requests[0].Authorization?.Parameter);
        Assert.Equal("https://second.test/api/v1/models", handler.Requests[1].Uri.AbsoluteUri);
        Assert.Equal("second-key", handler.Requests[1].Authorization?.Parameter);
    }

    [Fact]
    public async Task ChatClientDisposal_DoesNotDisposePackageConnection()
    {
        var context = CreateContext();
        var handler = new LMStudioTestHttpHandler((_, _, _) => LMStudioTestHttpHandler.Json("{\"data\":[]}"));
        using var connection = new LMStudioConnection(context, handler);
        using (var client = new LMStudioChatClient(
                   new AgentChatClientContext("lmstudio", "lmstudio/model"),
                   connection))
        {
        }

        var result = await new LMStudioModelCatalogService(connection).GetCatalogAsync();

        Assert.True(result.IsSuccess);
        Assert.False(handler.IsDisposed);
    }

    [Fact]
    public void ConnectionDisposal_DisposesOwnedHandler()
    {
        var handler = new LMStudioTestHttpHandler((_, _, _) => LMStudioTestHttpHandler.Json("{\"data\":[]}"));
        var connection = new LMStudioConnection(CreateContext(), handler);

        connection.Dispose();

        Assert.True(handler.IsDisposed);
    }

    [Fact]
    public async Task OptionalBearerKey_Clear_RemovesItFromFutureConnectionSnapshots()
    {
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = LMStudioProviderConfiguration.DefaultBaseUrl,
            },
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.ApiKeyKey] = "local-key",
            });
        var credentials = new ProviderCredentialAccessor(context.Secrets, LMStudioProviderConfiguration.ApiKeyKey);
        using var settings = new LMStudioSettingsViewModel(context, credentials);
        using var connection = new LMStudioConnection(
            context,
            credentials,
            new LMStudioTestHttpHandler((_, _, _) => LMStudioTestHttpHandler.Json("{\"data\":[]}")));
        await settings.InitializeAsync();

        Assert.Equal("local-key", (await connection.GetRequiredOptionsAsync()).ApiKey);
        settings.ApiKeySettings.RequestClearCredentialCommand.Execute(null);
        await settings.ApiKeySettings.ClearCredentialCommand.ExecuteAsync(null);

        Assert.Null(await context.Secrets.GetSecretAsync(LMStudioProviderConfiguration.ApiKeyKey));
        Assert.Null((await connection.GetRequiredOptionsAsync()).ApiKey);
    }

    [Fact]
    public void AuthenticatedNonLoopbackHttp_IsRejectedButLoopbackHttpIsAllowed()
    {
        Assert.False(LMStudioConnectionOptions.TryCreate(
            "http://lmstudio.test/v1",
            "secret",
            TimeSpan.FromSeconds(1),
            out _,
            out var error));
        Assert.Contains("HTTPS", error, StringComparison.Ordinal);

        Assert.True(LMStudioConnectionOptions.TryCreate(
            "http://127.0.0.1:1234/v1",
            "secret",
            TimeSpan.FromSeconds(1),
            out _,
            out _));
    }

    private static ProviderTestPackageContext CreateContext()
        => new(
            "sunder.package.agent.provider.lmstudio",
            new Dictionary<string, string>
            {
                [LMStudioProviderConfiguration.BaseUrlKey] = "http://lmstudio.test/v1",
            });
}
