using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Provider.Shared;

internal sealed record ProviderUtilityModelSelection(
    string ConfigurationKey,
    string DefaultModelId,
    IReadOnlyList<PackageSettingsOption> Options,
    Func<string?, string?>? Normalize = null)
{
    public async ValueTask<string> ResolveAsync(
        IPackageSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configured = await settings.GetValueAsync(ConfigurationKey, cancellationToken).ConfigureAwait(false);
        return Resolve(configured);
    }

    public string Resolve(string? configuredModelId)
        => UtilityModelSettingsState.ResolveModelId(
            Normalize?.Invoke(configuredModelId) ?? configuredModelId,
            DefaultModelId,
            Options.Select(option => option.Value));
}
