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
        foreach (var name in new[] { "AgentRpcCatalog", "AgentRunControlRegistry", "AgentChatWireMapper", "AgentChatFailureProjection" })
            Assert.Same(worker, worker.GetType("Sunder.Package.Agent.Protocol." + name, true)!.Assembly);
        foreach (var name in new[] { "AgentRpcServices", "AgentRpcContractDescriptors", "AgentRpcServiceHandler", "AgentChatProviderRpcClient", "AgentBehaviorLoopRuntimeRpcClient" })
            Assert.Null(worker.GetType("Sunder.Package.Agent.Protocol." + name));
    }

    internal static void AssertAgentAppSemanticOwnership(Assembly app)
    {
        Assert.DoesNotContain(app.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Protocol" or "Sunder.Agent.Execution.Common");
        Assert.DoesNotContain(app.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Worker" or "Microsoft.Data.Sqlite" or "Microsoft.Extensions.AI.Abstractions" or "Polly.Core");
        foreach (var name in new[] { "AgentProfileRecord", "AgentSessionRecord", "AgentTurnRecord", "AgentAttachmentUploadRequest", "AgentWorkspaceRecord" })
            Assert.Same(app, app.GetType("Sunder.Package.Agent.Contracts.Models." + name, true)!.Assembly);
        Assert.Same(app, app.GetType("Sunder.Package.Agent.Protocol.AgentRpcCatalog", true)!.Assembly);
        foreach (var name in new[] { "AgentRpcServices", "AgentRpcContractDescriptors", "AgentRunControlRegistry", "AgentChatWireMapper", "AgentChatStreamEvent" })
            Assert.Null(app.GetType("Sunder.Package.Agent.Protocol." + name));
    }
}
