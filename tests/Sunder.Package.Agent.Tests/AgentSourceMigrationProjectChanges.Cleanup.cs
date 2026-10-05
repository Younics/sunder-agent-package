using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeAgentCleanupBindings(string path, string text)
    {
        var normalized = path.Replace('\\', '/');
        var member = normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentSessionCleanupDispatcher.cs", StringComparison.Ordinal)
            || normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentSessionService.cs", StringComparison.Ordinal)
            ? "SessionCleaners"
            : normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentLifecycleDispatcher.cs", StringComparison.Ordinal)
                ? "DurableLifecycleObservers" : null;
        if (member is null) return text;
        var binding = "Sunder.Package.Agent.Runtime.Rpc.AgentCleanupBindings." + member;
        Assert.Equal(2, text.Split(binding, StringSplitOptions.None).Length);
        return text.Replace(binding, "AgentRpcServices." + member, StringComparison.Ordinal);
    }
}
