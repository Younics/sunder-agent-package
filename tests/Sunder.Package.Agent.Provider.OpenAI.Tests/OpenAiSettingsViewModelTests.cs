using System.Net;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class OpenAiSettingsViewModelTests
{
    [Fact]
    public async Task AuthMode_DefaultsToCodexConnectedAndPersistsSelection()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.openai");
        var viewModel = new OpenAiSettingsViewModel(context, new CodexConnectedAuthStrategy(context));

        Assert.Equal(OpenAiAuthMode.CodexConnected, viewModel.SelectedAuthMode?.ModeId);

        viewModel.SelectedAuthMode = viewModel.AuthModes.Single(mode => mode.ModeId == OpenAiAuthMode.ApiKey);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(OpenAiAuthMode.ApiKey, context.Storage.State.GetValue(OpenAiAuthMode.ConfigurationKey));
    }

    [Fact]
    public async Task ExpiredCachedSession_ThatCannotRefresh_IsNotReportedAsConnected()
    {
        var expiredSession = new OpenAiCodexSession(
            "expired-access",
            "refresh-token",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            "account-id");
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.openai",
            secretValues: new Dictionary<string, string>
            {
                ["auth.codex.session"] = JsonSerializer.Serialize(expiredSession),
            });
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                "{\"error\":\"temporarily_unavailable\"}",
                Encoding.UTF8,
                "application/json"),
        });
        using var strategy = new CodexConnectedAuthStrategy(
            context,
            () => new HttpClient(handler, disposeHandler: false),
            TimeProvider.System,
            TimeSpan.FromMinutes(10));
        using var viewModel = new OpenAiSettingsViewModel(context, strategy);

        await viewModel.RefreshStatusCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsCodexConnected);
        Assert.True(viewModel.IsCodexStatusError);
        Assert.True(viewModel.CanDisconnect);
        Assert.Equal("Session expired", viewModel.CodexStatusLabel);
    }

    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(response.StatusCode)
            {
                Content = response.Content,
            });
    }
}
