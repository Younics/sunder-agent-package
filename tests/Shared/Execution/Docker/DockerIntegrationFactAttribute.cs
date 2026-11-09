using Sunder.Package.Agent.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class DockerIntegrationFactAttribute : FactAttribute
{
    public DockerIntegrationFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "The Docker bind-mount integration test requires a Unix host.";
        }
        else if (!string.Equals(Environment.GetEnvironmentVariable("SUNDER_DOCKER_INTEGRATION"), "1", StringComparison.Ordinal)
                 && !TestPrerequisites.ExternalPrerequisitesAreRequired)
        {
            Skip = "Set SUNDER_DOCKER_INTEGRATION=1 and provide a reachable Docker daemon.";
        }
    }
}
