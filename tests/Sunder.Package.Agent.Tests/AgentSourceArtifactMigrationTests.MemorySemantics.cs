using System.Reflection;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static void AssertMemorySemanticOwnership(Assembly assembly, bool app)
    {
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Protocol" or "Sunder.Agent.Execution.Common");
        foreach (var name in new[] { "AgentSessionRecord", "AgentSessionContextCheckpointRecord", "AgentMemoryProvenance" })
            Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models." + name, true)!.Assembly);
        if (!app)
        {
            Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentProfileRecord", true)!.Assembly);
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Avalonia" or "CommunityToolkit.Mvvm"
                || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
        }
        else
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Worker" or "Microsoft.Data.Sqlite"
                || reference.Name!.StartsWith("SQLitePCLRaw", StringComparison.Ordinal));
            Assert.Null(assembly.GetType("Sunder.Package.Agent.Memory.Semantic.MemoryLocalStore"));
        }
        Assert.Equal(!app, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentDurableLifecycleEventEnvelope") is not null);
        Assert.Equal(!app, assembly.GetType("Sunder.Package.Agent.Contracts.Contracts.IAgentEmbeddingProvider") is not null);
        Assert.Equal(!app, assembly.GetType("Sunder.Package.Agent.Contracts.Contracts.IAgentRuntimeCatalog") is not null);
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Protocol.AgentRpcServices"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Protocol.AgentExecutionTargetRpc"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Memory.Semantic.Runtime.LegacySemanticMemoryRpcCatalog"));
    }
}
