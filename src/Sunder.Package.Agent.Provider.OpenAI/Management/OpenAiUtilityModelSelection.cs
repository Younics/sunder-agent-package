using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Provider.OpenAI;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
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
        => configuredModelId?.Trim();

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
