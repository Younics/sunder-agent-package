using System.Reflection;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static void AssertMcpSemanticOwnership(Assembly assembly, bool app)
    {
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Protocol" or "Sunder.Agent.Execution.Common");
        foreach (var name in new[] { "AgentProfileSelectableCapabilityDescriptor", "AgentToolDescriptor" })
            Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models." + name, true)!.Assembly);
        Assert.Equal(!app, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentRuntimeTool") is not null);
        Assert.Equal(!app, assembly.GetType("Sunder.Package.Agent.Contracts.Contracts.IAgentObservedToolExecution") is not null);
        if (app) Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentModelDescriptor", true)!.Assembly);
        if (!app)
        {
            Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentProfileRecord", true)!.Assembly);
            Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentWorkspaceRecord", true)!.Assembly);
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "CommunityToolkit.Mvvm" or "Sunder.Sdk.Avalonia"
                || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal) || reference.Name.StartsWith("TextMateSharp", StringComparison.Ordinal));
        }
    }
}
