using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal sealed record OpenAiUtilityModelSelection(
    string ConfigurationKey,
    string DefaultModelId,
    IReadOnlyList<PackageSettingsOption> Options)
{
    internal async ValueTask<string> ResolveAsync(
        IPackageSettings settings,
        CancellationToken cancellationToken = default)
        => Resolve(await settings.GetValueAsync(ConfigurationKey, cancellationToken));

    internal string? Normalize(string? configuredModelId)
        => OpenAiProviderConfiguration.NormalizeLegacyUtilityModelId(configuredModelId);

    internal string Resolve(string? configuredModelId)
    {
        var normalized = Normalize(configuredModelId)?.Trim();
        return !string.IsNullOrWhiteSpace(normalized)
               && Options.Any(option => string.Equals(
                   option.Value,
                   normalized,
                   StringComparison.OrdinalIgnoreCase))
            ? normalized
            : DefaultModelId;
    }
}
