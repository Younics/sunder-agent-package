using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeAgentMetadataBindings(string path, string text)
    {
        var normalized = path.Replace('\\', '/');
        var member = normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentProfileService.cs", StringComparison.Ordinal)
            ? "ProfileCapabilityConsumers"
            : normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentSystemPromptComposer.cs", StringComparison.Ordinal)
                ? "SystemPromptContributors"
                : normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentPermissionService.cs", StringComparison.Ordinal)
                    ? "PermissionSurfaces" : null;
        if (member is null) return text;
        var binding = "Sunder.Package.Agent.Runtime.Rpc.AgentMetadataBindings." + member;
        Assert.Equal(2, text.Split(binding, StringSplitOptions.None).Length);
        return text.Replace(binding, "AgentRpcServices." + member, StringComparison.Ordinal);
    }
}
