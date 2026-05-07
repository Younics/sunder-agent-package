using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Tools.Web;

public static class WebToolsConfiguration
{
    public static PackageConfigurationSchema Schema { get; } = new(
        "sunder.package.agent.tools.web",
        "Sunder Agent Tools Web",
        "Configure default web search behavior and optional Exa credentials for the Agent web tools.",
        [
            new PackageConfigurationSection(
                "search",
                "Search Defaults",
                "Set shared defaults for the canonical web_search tool.",
                [
                    new PackageConfigurationField(
                        "search.maxResults.default",
                        "Default max results",
                        PackageConfigurationFieldKind.Text,
                        Description: "Default number of Exa-backed web search results returned when a request does not specify one.",
                        DefaultValue: "5",
                        Placeholder: "5"
                    )
                ]
            ),
            new PackageConfigurationSection(
                "secrets",
                "Secrets",
                "Optional credentials used by the Exa-backed web_search tool.",
                [
                    new PackageConfigurationField(
                        "search.exa.apiKey",
                        "Optional Exa API key",
                        PackageConfigurationFieldKind.Secret,
                        Description: "Optional. If provided, Sunder will use your Exa API key; otherwise it will use the default Exa MCP-backed route.",
                        Placeholder: "exa_..."
                    )
                ]
            )
        ]
    );
}
