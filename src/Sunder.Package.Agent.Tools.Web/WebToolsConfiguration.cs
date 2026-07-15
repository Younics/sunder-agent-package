using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Tools.Web;

public static class WebToolsConfiguration
{
    public static PackageSettingsSchema Schema { get; } = new(
        "Configure default web search behavior and optional Exa credentials for the Agent web tools.",
        [
            new PackageSettingsSection(
                "search",
                "Search Defaults",
                "Set shared defaults for the canonical web_search tool.",
                [
                    new PackageSettingsField(
                        "search.maxResults.default",
                        "Default max results",
                        PackageSettingsFieldKind.Text,
                        description: "Default number of Exa-backed web search results returned when a request does not specify one.",
                        defaultValue: "5",
                        placeholder: "5"
                    )
                ]
            ),
            new PackageSettingsSection(
                "secrets",
                "Secrets",
                "Optional credentials used by the Exa-backed web_search tool.",
                [
                    new PackageSettingsField(
                        "search.exa.apiKey",
                        "Optional Exa API key",
                        PackageSettingsFieldKind.Secret,
                        description: "Optional. If provided, Sunder will use your Exa API key; otherwise it will use the default Exa MCP-backed route.",
                        placeholder: "exa_..."
                    )
                ]
            )
        ]
    );
}
