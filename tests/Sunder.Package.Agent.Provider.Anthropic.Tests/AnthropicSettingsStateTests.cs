using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;
using Sunder.Sdk.Storage;
using Xunit;

namespace Sunder.Package.Agent.Provider.Anthropic.Tests;

public sealed class AnthropicSettingsStateTests
{
    [Fact]
    public async Task ApiKey_AppReceivesPresenceOnlyAndSetRetainClearAreExplicitCommands()
    {
        const string storedCanary = "runtime-stored-secret-canary";
        const string replacementCanary = "replacement-command-secret-canary";
        var context = new ProviderTestPackageContext(
            "sunder.package.agent.provider.anthropic",
            secretValues: new Dictionary<string, string>
            {
                [AnthropicProviderConfiguration.ApiKeySecretKey] = storedCanary,
            });
        var runtime = CreateCredentialRuntime(context);
        using var viewModel = new AnthropicSettingsViewModel(context, runtime);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.ApiKeySettings.HasStoredCredential);
        Assert.Null(viewModel.ApiKeySettings.EnteredCredential);
        Assert.All(runtime.Invocations, invocation =>
            Assert.DoesNotContain(storedCanary, invocation.ResponseJson, StringComparison.Ordinal));

        viewModel.ApiKeySettings.EnteredCredential = "   ";
        await viewModel.ApiKeySettings.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Equal(storedCanary, await context.Secrets.GetSecretAsync(AnthropicProviderConfiguration.ApiKeySecretKey));
        Assert.Null(viewModel.ApiKeySettings.EnteredCredential);

        viewModel.ApiKeySettings.EnteredCredential = $" {replacementCanary} ";
        await viewModel.ApiKeySettings.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Equal(replacementCanary, await context.Secrets.GetSecretAsync(AnthropicProviderConfiguration.ApiKeySecretKey));
        Assert.Null(viewModel.ApiKeySettings.EnteredCredential);
        var setCommand = Assert.Single(runtime.Invocations, invocation =>
            invocation.OperationId == ProviderCredentialRuntimeOperations.Command.OperationId
            && invocation.RequestJson.Contains(replacementCanary, StringComparison.Ordinal));
        Assert.DoesNotContain(replacementCanary, setCommand.ResponseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(
            runtime.Invocations,
            invocation => invocation.OperationId == ProviderCredentialRuntimeOperations.Query.OperationId
                && invocation.RequestJson.Contains(replacementCanary, StringComparison.Ordinal));

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
            CreateCredentialRuntime(context));
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
        using var viewModel = new AnthropicSettingsViewModel(context, NullPackageRuntimeClient.Instance);
        viewModel.UtilityModelSettings.SelectedUtilityModel = viewModel.UtilityModelSettings.UtilityModels.Last();

        var save = viewModel.UtilityModelSettings.SaveUtilityModelCommand.ExecuteAsync(null);
        await state.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.UtilityModelSettings.CancelUtilityModelSaveCommand.Execute(null);
        await save;

        Assert.True(viewModel.UtilityModelSettings.IsOperationStatusWarning);
        Assert.Contains("canceled", viewModel.UtilityModelSettings.OperationStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await state.GetValueAsync(AnthropicProviderConfiguration.UtilityModelKey));
    }

    private static ProviderTestRuntimeClient CreateCredentialRuntime(ProviderTestPackageContext context)
    {
        var handler = new ProviderCredentialRuntimeHandler(new ProviderCredentialAccessor(
            context.Secrets,
            AnthropicProviderConfiguration.ApiKeySecretKey));
        return new ProviderTestRuntimeClient(async (operationId, request, cancellationToken) =>
        {
            if (operationId == ProviderCredentialRuntimeOperations.Query.OperationId)
            {
                return await handler.HandleAsync((ProviderCredentialQuery)request, cancellationToken);
            }

            if (operationId == ProviderCredentialRuntimeOperations.Command.OperationId)
            {
                return await handler.HandleAsync((ProviderCredentialCommand)request, cancellationToken);
            }

            throw new InvalidOperationException($"Unexpected Runtime operation '{operationId}'.");
        });
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
