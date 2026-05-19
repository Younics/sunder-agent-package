using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "sunder.package.agent.builder",
    Name = "Sunder Agent Builder",
    Summary = "Adds Sunder package development session controls to Sunder Agent.",
    Icon = "assets/icon.png"
)]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=1.0.0")]
