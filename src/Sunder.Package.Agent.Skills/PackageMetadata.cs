using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "sunder.package.agent.skills",
    Name = "Sunder Agent Skills",
    Summary = "Adds reusable skill support for Sunder Agent.",
    Icon = "assets/icon.png")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=2.0.0 <3.0.0")]
