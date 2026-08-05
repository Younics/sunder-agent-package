using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.Shared;

internal sealed class ApiKeyUtilitySettingsState : IDisposable
{
    public ApiKeyUtilitySettingsState(
        IPackageContext packageContext,
        IProviderCredentialSettingsGateway credentials,
        string credentialDescription,
        string credentialPlaceholder,
        Func<bool, ApiKeyStatus> resolveCredentialStatus,
        ProviderUtilityModelSelection utilityModelSelection)
    {
        ApiKey = new ApiKeySettingsState(
            credentials,
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
