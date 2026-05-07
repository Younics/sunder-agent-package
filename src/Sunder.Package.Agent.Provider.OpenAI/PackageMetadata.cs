using Sunder.Sdk.Packaging;

[assembly: SunderPackage(
    Id = "sunder.package.agent.provider.openai",
    Name = "Sunder Agent Provider OpenAI",
    Summary = "Adds OpenAI model providers to Sunder Agent.",
    Icon = "assets/icon.png")]

[assembly: SunderPackageDependency(
    PackageId = "sunder.package.agent",
    VersionRange = ">=1.0.0")]
