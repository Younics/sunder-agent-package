using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Provider.Anthropic.Tests;

public sealed class AnthropicSettingsStateTests
{
    [Fact]
    public async Task ApiKey_SetRetainReplaceAndDelete_AreExplicitAndDoNotRetainInput()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.anthropic");
        var credentials = new ProviderCredentialAccessor(
            context.Secrets,
            AnthropicProviderConfiguration.ApiKeySecretKey);
        using var viewModel = new AnthropicSettingsViewModel(context, credentials);

        viewModel.ApiKeySettings.EnteredCredential = " first-key ";
        await viewModel.ApiKeySettings.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Equal("first-key", context.Secrets.GetSecret(AnthropicProviderConfiguration.ApiKeySecretKey));
        Assert.Null(viewModel.ApiKeySettings.EnteredCredential);

        viewModel.ApiKeySettings.EnteredCredential = "   ";
        await viewModel.ApiKeySettings.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Equal("first-key", context.Secrets.GetSecret(AnthropicProviderConfiguration.ApiKeySecretKey));
        Assert.Null(viewModel.ApiKeySettings.EnteredCredential);

        viewModel.ApiKeySettings.EnteredCredential = "second-key";
        await viewModel.ApiKeySettings.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Equal("second-key", context.Secrets.GetSecret(AnthropicProviderConfiguration.ApiKeySecretKey));
        Assert.Null(viewModel.ApiKeySettings.EnteredCredential);

        viewModel.ApiKeySettings.RequestClearCredentialCommand.Execute(null);
        Assert.True(viewModel.ApiKeySettings.IsClearConfirmationRequested);
        await viewModel.ApiKeySettings.ClearCredentialCommand.ExecuteAsync(null);

        Assert.Null(context.Secrets.GetSecret(AnthropicProviderConfiguration.ApiKeySecretKey));
        Assert.False(viewModel.ApiKeySettings.HasStoredCredential);
        Assert.Null(viewModel.ApiKeySettings.EnteredCredential);
    }

    [Fact]
    public async Task ApiKey_SaveFailure_IsReportedAndLeavesNewInputForRetry()
    {
        var secrets = new ThrowingSecrets();
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.anthropic",
            secrets: secrets);
        using var viewModel = new AnthropicSettingsViewModel(
            context,
            new ProviderCredentialAccessor(secrets, AnthropicProviderConfiguration.ApiKeySecretKey));
        viewModel.ApiKeySettings.EnteredCredential = "retry-key";

        await viewModel.ApiKeySettings.SaveCredentialCommand.ExecuteAsync(null);

        Assert.True(viewModel.ApiKeySettings.IsOperationStatusError);
        Assert.Contains("could not be saved", viewModel.ApiKeySettings.OperationStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("retry-key", viewModel.ApiKeySettings.EnteredCredential);
    }

    [Fact]
    public async Task UtilityModelSave_CanBeCanceledWithoutChangingStoredValue()
    {
        var state = new BlockingKeyValueStore();
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.anthropic",
            state: state);
        using var viewModel = new AnthropicSettingsViewModel(context);
        viewModel.UtilityModelSettings.SelectedUtilityModel = viewModel.UtilityModelSettings.UtilityModels.Last();

        var save = viewModel.UtilityModelSettings.SaveUtilityModelCommand.ExecuteAsync(null);
        await state.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.UtilityModelSettings.CancelUtilityModelSaveCommand.Execute(null);
        await save;

        Assert.True(viewModel.UtilityModelSettings.IsOperationStatusWarning);
        Assert.Contains("canceled", viewModel.UtilityModelSettings.OperationStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Null(state.GetValue(AnthropicProviderConfiguration.UtilityModelKey));
    }

    private sealed class ThrowingSecrets : IPackageSecrets
    {
        public string? GetSecret(string key) => null;

        public void SetSecret(string key, string value) => throw new InvalidOperationException("secret store unavailable");

        public void DeleteSecret(string key) => throw new InvalidOperationException("secret store unavailable");
    }

    private sealed class BlockingKeyValueStore : IPackageKeyValueStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        internal TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? GetValue(string key) => _values.GetValueOrDefault(key);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(GetValue(key));

        public async Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            _values[key] = value;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.ContainsKey(key));

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_values.Keys.ToArray());
    }
}
