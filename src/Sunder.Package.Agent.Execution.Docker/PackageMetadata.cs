using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "sunder.package.agent.execution.docker",
    Name = "Sunder Agent Execution Docker",
    Summary = "Adds Docker-backed execution targets for Sunder Agent.",
    Icon = "assets/icon.png")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=2.0.0 <3.0.0")]
