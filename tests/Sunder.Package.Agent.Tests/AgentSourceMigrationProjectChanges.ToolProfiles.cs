using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeAgentToolProfileBindings(string path, string text)
    {
        if (!path.Replace('\\', '/').EndsWith("/src/Sunder.Package.Agent/Services/AgentToolService.Invocation.cs", StringComparison.Ordinal)) return text;
        const string binding = "Sunder.Package.Agent.Runtime.Rpc.AgentToolProfileBindings.Service";
        Assert.Equal(2, text.Split(binding, StringSplitOptions.None).Length);
        return text.Replace(binding, "AgentRpcServices.RuntimeCatalogs", StringComparison.Ordinal);
    }
}
