using Sunder.Package.Agent.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class PackageArtifactsFactAttribute : FactAttribute
{
    public PackageArtifactsFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(PackageFamilyArchiveContractTests.ArchiveDirectoryVariable))
            && !TestPrerequisites.ExternalPrerequisitesAreRequired)
        {
            Skip = $"Set {PackageFamilyArchiveContractTests.ArchiveDirectoryVariable} to the complete package archive directory.";
        }
    }
}
