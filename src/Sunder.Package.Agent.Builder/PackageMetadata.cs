using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "sunder.package.agent.builder",
    Name = "Sunder Agent Builder",
    Summary = "Creates, builds, and publishes Sunder package projects from Agent workspaces.",
    Icon = "assets/icon.png"
)]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=2.0.0 <3.0.0")]
