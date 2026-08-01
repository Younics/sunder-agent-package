using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "sample.sunder.agent.extension.minimal",
    Name = "Minimal Agent Extension",
    Summary = "Demonstrates a fixed read-only Agent tool.")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=2.0.0 <3.0.0")]
