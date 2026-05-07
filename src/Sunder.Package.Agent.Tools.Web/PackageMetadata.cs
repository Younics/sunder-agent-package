using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "sunder.package.agent.tools.web",
    Name = "Sunder Agent Tools Web",
    Summary = "Adds web search and fetch tools to Sunder Agent.",
    Icon = "assets/icon.png")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=1.0.0")]
