using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "sunder.package.agent.tools.shell",
    Name = "Sunder Agent Tools Shell",
    Summary = "Adds shell command tools to Sunder Agent.",
    Icon = "assets/icon.png")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=1.0.0")]
