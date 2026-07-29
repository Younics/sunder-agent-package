using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Storage;
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
        Assert.Equal("first-key", await context.Secrets.GetSecretAsync(AnthropicProviderConfiguration.ApiKeySecretKey));
        Assert.Null(viewModel.ApiKeySettings.EnteredCredential);

        viewModel.ApiKeySettings.EnteredCredential = "   ";
        await viewModel.ApiKeySettings.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Equal("first-key", await context.Secrets.GetSecretAsync(AnthropicProviderConfiguration.ApiKeySecretKey));
        Assert.Null(viewModel.ApiKeySettings.EnteredCredential);

        viewModel.ApiKeySettings.EnteredCredential = "second-key";
        await viewModel.ApiKeySettings.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Equal("second-key", await context.Secrets.GetSecretAsync(AnthropicProviderConfiguration.ApiKeySecretKey));
        Assert.Null(viewModel.ApiKeySettings.EnteredCredential);

        viewModel.ApiKeySettings.RequestClearCredentialCommand.Execute(null);
        Assert.True(viewModel.ApiKeySettings.IsClearConfirmationRequested);
        await viewModel.ApiKeySettings.ClearCredentialCommand.ExecuteAsync(null);

        Assert.Null(await context.Secrets.GetSecretAsync(AnthropicProviderConfiguration.ApiKeySecretKey));
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
            settings: state);
        using var viewModel = new AnthropicSettingsViewModel(context);
        viewModel.UtilityModelSettings.SelectedUtilityModel = viewModel.UtilityModelSettings.UtilityModels.Last();

        var save = viewModel.UtilityModelSettings.SaveUtilityModelCommand.ExecuteAsync(null);
        await state.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.UtilityModelSettings.CancelUtilityModelSaveCommand.Execute(null);
        await save;

        Assert.True(viewModel.UtilityModelSettings.IsOperationStatusWarning);
        Assert.Contains("canceled", viewModel.UtilityModelSettings.OperationStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await state.GetValueAsync(AnthropicProviderConfiguration.UtilityModelKey));
    }

    private sealed class ThrowingSecrets : IPackageSecrets
    {
        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            return Task.FromResult<string?>(null);
        }

        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            ValidateValue(value);
            throw new InvalidOperationException("secret store unavailable");
        }

        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            throw new InvalidOperationException("secret store unavailable");
        }
    }

    private sealed class BlockingKeyValueStore : IPackageKeyValueStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        internal TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public async Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            ValidateValue(value);
            WriteStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            _values[key] = value;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            return Task.FromResult(_values.ContainsKey(key));
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateKey(key);
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
        {
            if (prefix is not null && prefix.Length != 0 && !PackageStorageValidation.IsValidKey(prefix))
            {
                throw new ArgumentException("Invalid test storage prefix.", nameof(prefix));
            }
            return Task.FromResult<IReadOnlyList<string>>(_values.Keys
                .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray());
        }
    }

    private static void ValidateKey(string? key)
    {
        if (!PackageStorageValidation.IsValidKey(key))
        {
            throw new ArgumentException("Invalid test storage key.", nameof(key));
        }
    }

    private static void ValidateValue(string? value)
    {
        if (!PackageStorageValidation.IsValidValue(value))
        {
            throw new ArgumentException("Invalid test storage value.", nameof(value));
        }
    }
}
