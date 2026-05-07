using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Mcp;

public static class McpPackageConfiguration
{
    public static PackageConfigurationSchema Schema { get; } = new(
        "sunder.package.agent.mcp",
        "Sunder Agent MCP",
        "Configure shared MCP servers once in Settings, then enable them per agent profile.",
        []);
}
