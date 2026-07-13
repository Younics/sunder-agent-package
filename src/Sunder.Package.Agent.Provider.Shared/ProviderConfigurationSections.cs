using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Provider.Shared;

internal static class ProviderConfigurationSections
{
    public static PackageConfigurationSection ApiKey(
        string key,
        string sectionDescription,
        string fieldDescription,
        string placeholder)
        => new(
            "authentication",
            "Authentication",
            sectionDescription,
            [
                new PackageConfigurationField(
                    key,
                    "API key",
                    PackageConfigurationFieldKind.Secret,
                    Description: fieldDescription,
                    Placeholder: placeholder),
            ]);

    public static PackageConfigurationSection UtilityModelSelect(
        ProviderUtilityModelSelection selection,
        string sectionDescription)
        => new(
            "utility",
            "Utility model",
            sectionDescription,
            [
                new PackageConfigurationField(
                    selection.ConfigurationKey,
                    "Utility model",
                    PackageConfigurationFieldKind.Select,
                    Description: "Used for short background tasks, not regular agent replies.",
                    IsRequired: true,
                    DefaultValue: selection.DefaultModelId,
                    Options: selection.Options),
            ]);

    public static PackageConfigurationSection UtilityModelText(
        string key,
        string sectionDescription,
        string fieldDescription,
        string placeholder)
        => new(
            "utility",
            "Utility model",
            sectionDescription,
            [
                new PackageConfigurationField(
                    key,
                    "Utility model ID",
                    PackageConfigurationFieldKind.Text,
                    Description: fieldDescription,
                    Placeholder: placeholder),
            ]);
}
