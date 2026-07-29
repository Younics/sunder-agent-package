using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Anthropic;

public sealed partial class AnthropicSettingsViewModel : ObservableObject,
    IPackageViewNavigationPreparationTarget,
    IDisposable
{
    private readonly ApiKeyUtilitySettingsState _settings;
    private readonly object _initializationSyncRoot = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _initialization;
    private bool _disposed;

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } =
        [AnthropicProviderConfiguration.ApiKeySecretKey, AnthropicProviderConfiguration.UtilityModelKey];

    public AnthropicSettingsViewModel(IPackageContext packageContext)
        : this(
            packageContext,
            new ProviderCredentialAccessor(packageContext.Secrets, AnthropicProviderConfiguration.ApiKeySecretKey))
    {
    }

    internal AnthropicSettingsViewModel(
        IPackageContext packageContext,
        ProviderCredentialAccessor credentials)
    {
        _settings = new ApiKeyUtilitySettingsState(
            packageContext,
            credentials,
            "A stored API key is used for Claude chat. Blank input retains the current key.",
            "sk-ant-...",
            static hasCredential => hasCredential
                ? new ApiKeyStatus("Stored", "Anthropic API-key chat is ready.")
                : new ApiKeyStatus("Not stored", "Add an API key to enable Claude chat.", IsWarning: true),
            AnthropicProviderConfiguration.UtilityModelSelection);
    }

    internal ApiKeySettingsState ApiKeySettings => _settings.ApiKey;

    internal UtilityModelSettingsState UtilityModelSettings => _settings.UtilityModel;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task initialization;
        lock (_initializationSyncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            initialization = _initialization ??= _settings.InitializeAsync(_lifetime.Token);
        }
        return cancellationToken.CanBeCanceled
            ? initialization.WaitAsync(cancellationToken)
            : initialization;
    }

    public async ValueTask<bool> PrepareNavigationAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return true;
    }

    public ValueTask OnNavigationPresentedAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        await _settings.SaveAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _lifetime.Cancel();
        _settings.Dispose();
        _lifetime.Dispose();
    }
}
