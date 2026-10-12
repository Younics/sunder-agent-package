using System.Reflection;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static void AssertSkillsSemanticOwnership(Assembly assembly, bool app)
    {
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Protocol" or "Sunder.Agent.Execution.Common");
        foreach (var name in new[] { "AgentProfileSelectableCapabilityDescriptor", "AgentToolDescriptor" })
            AssertImportedRoleModel(assembly, name);
        Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Skills.Services.InstalledSkillRecord", true)!.Assembly);
        AssertImportedRoleModel(assembly, "AgentPromptContextRequest");
        AssertImportedRoleModel(assembly, "AgentResolvedExecutionResource");
        if (app)
        {
            AssertImportedRoleModel(assembly, "AgentModelDescriptor");
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Worker" or "Sunder.Sdk.Stacks");
        }
        else
        {
            AssertImportedRoleModel(assembly, "AgentProfileRecord");
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Avalonia" or "CommunityToolkit.Mvvm"
                || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
        }
    }
}
