using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "sunder.package.agent.mcp",
    Name = "Sunder Agent MCP",
    Summary = "Adds Model Context Protocol tool integration for Sunder Agent.",
    Icon = "assets/icon.png")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=1.0.0")]
