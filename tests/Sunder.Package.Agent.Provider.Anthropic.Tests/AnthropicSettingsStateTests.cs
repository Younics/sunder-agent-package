extern alias AnthropicApp;

using System.Text.Json;
using Sunder.Package.Agent.Provider.Anthropic.Rpc;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Storage;
using Xunit;
using AnthropicSettingsViewModel = AnthropicApp::Sunder.Package.Agent.Provider.Anthropic.AnthropicSettingsViewModel;
using ICredentialSettingsService = AnthropicApp::Sunder.Generated.Packages.Younics_Ai_Agent_Provider_Anthropic_Services.ICredentialSettingsService;

namespace Sunder.Package.Agent.Provider.Anthropic.Tests;

public sealed class AnthropicSettingsStateTests
{
    [Fact]
    public async Task ApiKey_AppReceivesPresenceOnlyAndSetRetainClearAreExplicitCommands()
    {
        const string storedCanary = "runtime-stored-secret-canary";
        const string replacementCanary = "replacement-command-secret-canary";
        var context = new ProviderTestPackageContext(
            "younics.ai.agent.provider.anthropic",
            secretValues: new Dictionary<string, string>
            {
                [AnthropicProviderConfiguration.ApiKeySecretKey] = storedCanary,
            });
        var ownerClient = new CredentialOwnerClientFake(context);
        using var viewModel = new AnthropicSettingsViewModel(context, ownerClient);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.ApiKeySettings.HasStoredCredential);
        Assert.Null(viewModel.ApiKeySettings.EnteredCredential);
        Assert.All(ownerClient.Invocations, invocation =>
            Assert.DoesNotContain(storedCanary, invocation.ResponseJson, StringComparison.Ordinal));

        viewModel.ApiKeySettings.EnteredCredential = "   ";
        await viewModel.ApiKeySettings.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Equal(storedCanary, await context.Secrets.GetSecretAsync(AnthropicProviderConfiguration.ApiKeySecretKey));
        Assert.Null(viewModel.ApiKeySettings.EnteredCredential);

        viewModel.ApiKeySettings.EnteredCredential = $" {replacementCanary} ";
        await viewModel.ApiKeySettings.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Equal(replacementCanary, await context.Secrets.GetSecretAsync(AnthropicProviderConfiguration.ApiKeySecretKey));
        Assert.Null(viewModel.ApiKeySettings.EnteredCredential);
        var setCommand = Assert.Single(ownerClient.Invocations, invocation =>
            invocation.MethodId == "set"
            && invocation.RequestJson.Contains(replacementCanary, StringComparison.Ordinal));
        Assert.DoesNotContain(replacementCanary, setCommand.ResponseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ownerClient.Invocations,
            invocation => invocation.MethodId == "get-status"
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
            "younics.ai.agent.provider.anthropic",
            secrets: secrets);
        using var viewModel = new AnthropicSettingsViewModel(
            context,
            new CredentialOwnerClientFake(context));
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
            "younics.ai.agent.provider.anthropic",
            settings: state);
        using var viewModel = new AnthropicSettingsViewModel(context, new CredentialOwnerClientFake(context));
        viewModel.UtilityModelSettings.SelectedUtilityModel = viewModel.UtilityModelSettings.UtilityModels.Last();

        var save = viewModel.UtilityModelSettings.SaveUtilityModelCommand.ExecuteAsync(null);
        await state.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.UtilityModelSettings.CancelUtilityModelSaveCommand.Execute(null);
        await save;

        Assert.True(viewModel.UtilityModelSettings.IsOperationStatusWarning);
        Assert.Contains("canceled", viewModel.UtilityModelSettings.OperationStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await state.GetValueAsync(AnthropicProviderConfiguration.UtilityModelKey));
    }

    private sealed record CredentialOwnerInvocation(
        string MethodId,
        string RequestJson,
        string ResponseJson);

    private sealed class CredentialOwnerClientFake : ICredentialSettingsService
    {
        private readonly ISunderRpcServiceHandler _provider;
        private readonly List<CredentialOwnerInvocation> _invocations = [];

        internal CredentialOwnerClientFake(ProviderTestPackageContext context)
        {
            _provider = new CredentialSettingsService(new ProviderCredentialAccessor(
                context.Secrets,
                AnthropicProviderConfiguration.ApiKeySecretKey)).CreateSunderRpcProviderRegistration().Handler;
        }

        internal IReadOnlyList<CredentialOwnerInvocation> Invocations => _invocations;

        public ValueTask<bool> GetStatusAsync(
            CancellationToken cancellationToken = default)
            => InvokeAsync("get-status", JsonSerializer.SerializeToElement(new { }), cancellationToken);

        public ValueTask<bool> SetAsync(
            string credential,
            CancellationToken cancellationToken = default)
            => InvokeAsync("set", JsonSerializer.SerializeToElement(credential), cancellationToken);

        public ValueTask<bool> ClearAsync(
            CancellationToken cancellationToken = default)
            => InvokeAsync("clear", JsonSerializer.SerializeToElement(new { }), cancellationToken);

        private async ValueTask<bool> InvokeAsync(string methodId, JsonElement request, CancellationToken cancellationToken)
        {
            var response = await _provider.InvokeUnaryAsync(null!, "credential-settings", methodId, request, cancellationToken);
            _invocations.Add(new CredentialOwnerInvocation(methodId, request.GetRawText(), response.GetRawText()));
            return response.GetBoolean();
        }
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
