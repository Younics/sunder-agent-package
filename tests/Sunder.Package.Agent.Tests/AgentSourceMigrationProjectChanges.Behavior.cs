using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeAgentBehaviorBindings(string path, string text)
    {
        if (!path.Replace('\\', '/').EndsWith("/src/Sunder.Package.Agent/PackageModule.cs", StringComparison.Ordinal)) return text;
        const string current = "Runtime.Rpc.AgentBehaviorBindings.Create(";
        Assert.Equal(2, text.Split(current, StringSplitOptions.None).Length);
        return text.Replace(current, "AgentRpcServices.CreateBehaviorLoops(", StringComparison.Ordinal);
    }
}
