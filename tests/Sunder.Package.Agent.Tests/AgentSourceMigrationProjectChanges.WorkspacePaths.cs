using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeAgentWorkspacePathBindings(string path, string text)
    {
        if (!path.Replace('\\', '/').EndsWith("/src/Sunder.Package.Agent/Services/AgentWorkspaceService.cs", StringComparison.Ordinal)) return text;
        const string binding = "Sunder.Package.Agent.Runtime.Rpc.AgentWorkspacePathBindings.Service";
        Assert.Equal(2, text.Split(binding, StringSplitOptions.None).Length);
        return text.Replace(binding, "AgentRpcServices.WorkspacePathMigrators", StringComparison.Ordinal);
    }
}
