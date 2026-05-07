using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "sunder.package.agent.memory.semantic",
    Name = "Sunder Agent Memory Semantic",
    Summary = "Adds semantic memory indexing and recall for Sunder Agent.",
    Icon = "assets/icon.png")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=1.0.0")]
