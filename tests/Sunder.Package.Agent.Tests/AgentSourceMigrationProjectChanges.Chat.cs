using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeAgentChatBindings(string path, string text)
    {
        var normalized = path.Replace('\\', '/');
        if (!new[] { "AgentRunProviderResolver.cs", "AgentProfileService.cs", "AgentProfileStackContributor.cs" }
            .Any(file => normalized.EndsWith("/src/Sunder.Package.Agent/Services/" + file, StringComparison.Ordinal))) return text;
        const string binding = "Sunder.Package.Agent.Runtime.Rpc.AgentChatBindings.Service";
        Assert.Equal(2, text.Split(binding, StringSplitOptions.None).Length);
        return text.Replace(binding, "AgentRpcServices.ChatProviders", StringComparison.Ordinal);
    }
}
