using System.Reflection;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static void AssertSkillsSemanticOwnership(Assembly assembly, bool app)
    {
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Protocol" or "Sunder.Agent.Execution.Common");
        foreach (var name in new[] { "AgentProfileSelectableCapabilityDescriptor", "AgentToolDescriptor" })
            Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models." + name, true)!.Assembly);
        Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Skills.Services.InstalledSkillRecord", true)!.Assembly);
        Assert.Equal(!app, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentPromptContextRequest") is not null);
        Assert.Equal(!app, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentResolvedExecutionResource") is not null);
        if (app)
        {
            Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentModelDescriptor", true)!.Assembly);
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Worker" or "Sunder.Sdk.Stacks");
        }
        else
        {
            Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentProfileRecord", true)!.Assembly);
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Avalonia" or "CommunityToolkit.Mvvm"
                || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
        }
    }
}
