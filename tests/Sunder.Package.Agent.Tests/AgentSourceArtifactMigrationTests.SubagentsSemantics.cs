using System.Reflection;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static void AssertSubagentsSemanticOwnership(Assembly assembly, bool app)
    {
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Protocol" or "Sunder.Agent.Execution.Common");
        foreach (var name in new[] { "AgentSessionRecord", "AgentTurnRecord", "AgentTranscriptToolDetailRecord", "AgentProfileRecord", "AgentModelDescriptor" })
            Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models." + name, true)!.Assembly);
        Assert.Equal(!app, assembly.GetType("Sunder.Package.Agent.Contracts.Contracts.IAgentChildRunExecutor") is not null);
        Assert.Equal(!app, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentBehaviorLoopContext") is not null);
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
