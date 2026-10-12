using System.Reflection;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static void AssertAgentWorkerSemanticOwnership(Assembly worker)
    {
        Assert.DoesNotContain(worker.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Protocol" or "Sunder.Agent.Execution.Common");
        Assert.DoesNotContain(worker.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Avalonia" or "CommunityToolkit.Mvvm" or "LiveMarkdown.Avalonia"
            || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
        foreach (var name in new[] { "AgentProfileRecord", "AgentSessionRecord", "AgentTurnRecord", "AgentToolExecutionContext", "AgentBehaviorLoopContext" })
            Assert.Same(worker, worker.GetType("Sunder.Package.Agent.Contracts.Models." + name, true)!.Assembly);
        Assert.Same(worker, worker.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Services.IAgentProfileService", true)!.Assembly);
    }

    internal static void AssertAgentAppSemanticOwnership(Assembly app)
    {
        Assert.DoesNotContain(app.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Protocol" or "Sunder.Agent.Execution.Common");
        Assert.DoesNotContain(app.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Worker" or "Microsoft.Data.Sqlite" or "Microsoft.Extensions.AI.Abstractions" or "Polly.Core");
        foreach (var name in new[] { "AgentProfileRecord", "AgentSessionRecord", "AgentTurnRecord", "AgentAttachmentUploadRequest", "AgentWorkspaceRecord" })
            Assert.Same(app, app.GetType("Sunder.Package.Agent.Contracts.Models." + name, true)!.Assembly);
        Assert.Same(app, app.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Services.IAgentRuntimeService", true)!.Assembly);
    }
}
