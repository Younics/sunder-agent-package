using System.Reflection;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static void AssertSubagentsSemanticOwnership(Assembly assembly, bool app)
    {
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Protocol" or "Sunder.Agent.Execution.Common");
        foreach (var name in new[] { "AgentSessionRecord", "AgentTurnRecord", "AgentTranscriptToolDetailRecord", "AgentProfileRecord", "AgentModelDescriptor" })
            AssertImportedRoleModel(assembly, name);
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Contracts.Contracts.IAgentChildRunExecutor"));
        Assert.Same(assembly, assembly.GetType(ImportedServices + "IChildRunExecutorService", true)!.Assembly);
        AssertImportedRoleModel(assembly, "AgentBehaviorLoopContext");
        if (app)
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Worker" or "Sunder.Sdk.Stacks" or "Microsoft.Extensions.AI.Abstractions");
        }
        else
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Avalonia" or "CommunityToolkit.Mvvm" or "LiveMarkdown.Avalonia"
                || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Protocol.AgentRpcServices"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Subagents.Runtime.LegacySubagentRuntimeBindings"));
    }
}
