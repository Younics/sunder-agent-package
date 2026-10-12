using System.Reflection;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static void AssertMcpSemanticOwnership(Assembly assembly, bool app)
    {
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Protocol" or "Sunder.Agent.Execution.Common");
        foreach (var name in new[] { "AgentProfileSelectableCapabilityDescriptor", "AgentToolDescriptor" })
            AssertImportedRoleModel(assembly, name);
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentRuntimeTool"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Contracts.Contracts.IAgentObservedToolExecution"));
        Assert.Same(assembly, assembly.GetType(ImportedServices + "IToolExecutionObservedService", true)!.Assembly);
        if (app) AssertImportedRoleModel(assembly, "AgentModelDescriptor");
        if (!app)
        {
            AssertImportedRoleModel(assembly, "AgentProfileRecord");
            AssertImportedRoleModel(assembly, "AgentWorkspaceRecord");
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "CommunityToolkit.Mvvm" or "Sunder.Sdk.Avalonia"
                || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal) || reference.Name.StartsWith("TextMateSharp", StringComparison.Ordinal));
        }
    }
}
