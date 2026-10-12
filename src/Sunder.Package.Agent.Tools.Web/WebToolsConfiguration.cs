using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Tools.Web;

public static class WebToolsConfiguration
{
    public static PackageSettingsSchema Schema { get; } = new(
        "Configure web search providers, defaults, and optional credentials for the Agent web tools.",
        [
            new PackageSettingsSection(
                "search",
                "Search Defaults",
                "Set shared defaults for the canonical web_search tool.",
                [
                    new PackageSettingsField(
                        "search.provider",
                        "Search provider",
                        PackageSettingsFieldKind.Select,
                        description: "Auto prefers Exa when an Exa key is configured; otherwise it uses free Parallel search. Auto tries the other provider when rate limited. Free search is subject to provider quotas.",
                        defaultValue: "auto",
                        options:
                        [
                            new PackageSettingsOption("auto", "Auto"),
                            new PackageSettingsOption("exa", "Exa"),
                            new PackageSettingsOption("parallel", "Parallel"),
                        ]),
                    new PackageSettingsField(
                        "search.maxResults.default",
                        "Default max results",
                        PackageSettingsFieldKind.Text,
                        description: "Default maximum number of web search results returned when a request does not specify one.",
                        defaultValue: "5",
                        placeholder: "5"
                    )
                ]
            ),
            new PackageSettingsSection(
                "secrets",
                "Secrets",
                "Optional credentials for higher search limits. Both providers support limited free search without a key.",
                [
                    new PackageSettingsField(
                        "search.exa.apiKey",
                        "Optional Exa API key",
                        PackageSettingsFieldKind.Secret,
                        description: "Optional. Uses your Exa account for Exa searches and makes Exa the preferred provider in Auto mode.",
                        placeholder: "exa_..."
                    ),
                    new PackageSettingsField(
                        "search.parallel.apiKey",
                        "Optional Parallel API key",
                        PackageSettingsFieldKind.Secret,
                        description: "Optional. Uses your Parallel account for Parallel searches; otherwise searches use Parallel's free MCP allowance.")
                ]
            )
        ]
    );
}
