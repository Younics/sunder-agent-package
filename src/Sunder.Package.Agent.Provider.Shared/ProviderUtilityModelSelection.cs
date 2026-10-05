using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Provider.Shared;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
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
        => ResolveModelId(
            Normalize?.Invoke(configuredModelId) ?? configuredModelId,
            DefaultModelId,
            Options.Select(option => option.Value));

    internal static string ResolveModelId(string? configuredModelId, string defaultModelId, IEnumerable<string> knownModelIds)
    {
        var configured = configuredModelId?.Trim();
        return !string.IsNullOrWhiteSpace(configured)
               && knownModelIds.Any(modelId => string.Equals(modelId, configured, StringComparison.OrdinalIgnoreCase))
            ? configured
            : defaultModelId;
    }
}
