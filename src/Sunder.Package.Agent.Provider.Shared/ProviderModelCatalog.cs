using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Provider.Shared;

internal sealed record ProviderModelCatalogSnapshot(
    IReadOnlyList<AgentModelDescriptor> Models,
    IReadOnlyList<PackageConfigurationOption> UtilityModelOptions);

internal static class ProviderModelCatalog
{
    public static ProviderModelCatalogSnapshot ValidateAndOrder(
        IEnumerable<AgentModelDescriptor> models,
        string defaultUtilityModelId,
        Func<AgentModelDescriptor, bool> isUtilityEligible)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultUtilityModelId);
        ArgumentNullException.ThrowIfNull(isUtilityEligible);

        var modelList = models.ToArray();
        var modelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in modelList)
        {
            if (string.IsNullOrWhiteSpace(model.ModelId) || !modelIds.Add(model.ModelId))
            {
                throw new InvalidOperationException($"Provider model IDs must be non-empty and unique; duplicate ID '{model.ModelId}'.");
            }

            if (string.IsNullOrWhiteSpace(model.DisplayName))
            {
                throw new InvalidOperationException($"Provider model '{model.ModelId}' must have a display name.");
            }

            if (model.ContextWindow <= 0 || model.MaxOutputTokens <= 0)
            {
                throw new InvalidOperationException($"Provider model '{model.ModelId}' must have positive context and output limits.");
            }

            ValidateUniqueOptions(model.ModelId, "variant", model.Variants, static option => option.VariantId);
            ValidateUniqueOptions(model.ModelId, "speed", model.SpeedOptions, static option => option.SpeedOptionId);
            ValidateUniqueOptions(model.ModelId, "mode", model.ModeOptions, static option => option.ModeOptionId);
        }

        var orderedModels = modelList
            .OrderByDescending(model => model.ReleaseDate.HasValue)
            .ThenByDescending(model => model.ReleaseDate)
            .ThenBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.DisplayName, StringComparer.Ordinal)
            .ThenBy(model => model.ModelId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.ModelId, StringComparer.Ordinal)
            .ToArray();
        var utilityModels = orderedModels.Where(isUtilityEligible).ToArray();
        if (!utilityModels.Any(model => string.Equals(model.ModelId, defaultUtilityModelId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Default utility model '{defaultUtilityModelId}' must exist and be explicitly utility-eligible.");
        }

        var utilityOptions = utilityModels
            .Select(model => new PackageConfigurationOption(model.ModelId, model.DisplayName))
            .ToArray();
        if (utilityOptions.Select(option => option.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != utilityOptions.Length)
        {
            throw new InvalidOperationException("Utility model option values must be unique.");
        }

        return new ProviderModelCatalogSnapshot(orderedModels, utilityOptions);
    }

    private static void ValidateUniqueOptions<TOption>(
        string modelId,
        string optionKind,
        IReadOnlyList<TOption>? options,
        Func<TOption, string> getId)
    {
        if (options is null)
        {
            return;
        }

        var optionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            var optionId = getId(option);
            if (string.IsNullOrWhiteSpace(optionId) || !optionIds.Add(optionId))
            {
                throw new InvalidOperationException(
                    $"Provider model '{modelId}' has a non-unique {optionKind} option ID '{optionId}'.");
            }
        }
    }
}
