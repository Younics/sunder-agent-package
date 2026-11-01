using System.Reflection;
using System.Runtime.Loader;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static Assembly LoadExecutionOwner(string worker, AssemblyLoadContext load)
    {
        using var bytes = File.OpenRead(Path.Combine(SingleProjectRoleOutput(Path.Combine(Path.GetDirectoryName(worker)!, "Worker"), "worker"), "Sunder.Package.Agent.dll"));
        return load.LoadFromStream(bytes);
    }

}
