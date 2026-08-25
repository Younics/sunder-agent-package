using Xunit;

namespace Sunder.Package.Agent.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute(string reason)
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = reason;
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute(string reason)
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = reason;
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class DockerIntegrationFactAttribute : FactAttribute
{
    public DockerIntegrationFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "The Docker bind-mount integration test requires a Unix host.";
        }
        else if (!string.Equals(
                     Environment.GetEnvironmentVariable("SUNDER_DOCKER_INTEGRATION"),
                     "1",
                     StringComparison.Ordinal)
                 && !TestPrerequisites.ExternalPrerequisitesAreRequired)
        {
            Skip = "Set SUNDER_DOCKER_INTEGRATION=1 and provide a reachable Docker daemon.";
        }
    }
}

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

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class FileSystemFactAttribute : FactAttribute
{
    public FileSystemFactAttribute(TestFileSystemCapability capability, string reason)
    {
        Skip = TestFileSystemCapabilities.GetUnavailableReason(capability, reason);
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class FileSystemTheoryAttribute : TheoryAttribute
{
    public FileSystemTheoryAttribute(TestFileSystemCapability capability, string reason)
    {
        Skip = TestFileSystemCapabilities.GetUnavailableReason(capability, reason);
    }
}
