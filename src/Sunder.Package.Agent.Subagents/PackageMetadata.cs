using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "sunder.package.agent.subagents",
    Name = "Sunder Agent Subagents",
    Summary = "Adds orchestrated subagents and child agent run coordination.",
    Icon = "assets/icon.png")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=1.0.0")]
