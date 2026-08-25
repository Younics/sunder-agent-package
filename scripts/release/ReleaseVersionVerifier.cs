using Sunder.Sdk.Packaging;

const string familyDependencyRange = ">=2.0.0 <3.0.0";

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: Sunder.Agent.ReleaseVersionVerifier <release-version>");
    return 2;
}

if (!SemanticVersion.TryParse(args[0], out var releaseVersion))
{
    Console.Error.WriteLine($"Agent release version '{args[0]}' is not strict SemVer 2.0.");
    return 1;
}

var familyRange = PackageVersionRange.Parse(familyDependencyRange);
if (!familyRange.IsSatisfiedBy(releaseVersion))
{
    Console.Error.WriteLine(
        $"Agent release version '{releaseVersion}' does not satisfy the family dependency range '{familyDependencyRange}'.");
    return 1;
}

return 0;
