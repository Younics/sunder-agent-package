using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Shared;

internal sealed class ApiKeyUtilitySettingsState : IDisposable
{
    public ApiKeyUtilitySettingsState(
        IPackageContext packageContext,
        Func<CancellationToken, ValueTask<bool>> getStatus,
        Func<string, CancellationToken, ValueTask<bool>> setCredential,
        Func<CancellationToken, ValueTask<bool>> clearCredential,
        string credentialDescription,
        string credentialPlaceholder,
        Func<bool, ApiKeyStatus> resolveCredentialStatus,
        ProviderUtilityModelSelection utilityModelSelection)
    {
        ApiKey = new ApiKeySettingsState(
            getStatus,
            setCredential,
            clearCredential,
            credentialDescription,
            credentialPlaceholder,
            resolveCredentialStatus);
        UtilityModel = new UtilityModelSettingsState(
            packageContext,
            utilityModelSelection.ConfigurationKey,
            utilityModelSelection.DefaultModelId,
            utilityModelSelection.Options.Select(option => (option.Value, option.Label)),
            utilityModelSelection.Normalize);
    }

    public ApiKeySettingsState ApiKey { get; }

    public UtilityModelSettingsState UtilityModel { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ApiKey.RefreshCredentialStatusAsync(cancellationToken);
        await UtilityModel.InitializeAsync(cancellationToken);
    }

    public async Task SaveAsync()
    {
        await ApiKey.SaveCredentialAsync();
        await UtilityModel.SaveUtilityModelAsync();
    }

    public void Dispose()
    {
        ApiKey.Dispose();
        UtilityModel.Dispose();
    }
}
