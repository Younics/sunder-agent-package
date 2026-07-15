using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Provider.Shared;

internal static class ProviderConfigurationSections
{
    public static PackageSettingsSection ApiKey(
        string key,
        string sectionDescription,
        string fieldDescription,
        string placeholder)
        => new(
            "authentication",
            "Authentication",
            sectionDescription,
            [
                new PackageSettingsField(
                    key,
                    "API key",
                    PackageSettingsFieldKind.Secret,
                    description: fieldDescription,
                    placeholder: placeholder),
            ]);

    public static PackageSettingsSection UtilityModelSelect(
        ProviderUtilityModelSelection selection,
        string sectionDescription)
        => new(
            "utility",
            "Utility model",
            sectionDescription,
            [
                new PackageSettingsField(
                    selection.ConfigurationKey,
                    "Utility model",
                    PackageSettingsFieldKind.Select,
                    description: "Used for short background tasks, not regular agent replies.",
                    isRequired: true,
                    defaultValue: selection.DefaultModelId,
                    options: selection.Options),
            ]);

    public static PackageSettingsSection UtilityModelText(
        string key,
        string sectionDescription,
        string fieldDescription,
        string placeholder)
        => new(
            "utility",
            "Utility model",
            sectionDescription,
            [
                new PackageSettingsField(
                    key,
                    "Utility model ID",
                    PackageSettingsFieldKind.Text,
                    description: fieldDescription,
                    placeholder: placeholder),
            ]);
}
