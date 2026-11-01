using System.Reflection;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static void AssertMemorySemanticOwnership(Assembly assembly, bool app)
    {
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Protocol" or "Sunder.Agent.Execution.Common");
        foreach (var name in new[] { "AgentSessionRecord", "AgentSessionContextCheckpointRecord" })
            AssertImportedRoleModel(assembly, name);
        Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Contracts.Models.AgentMemoryProvenance", true)!.Assembly);
        if (!app)
        {
            AssertImportedRoleModel(assembly, "AgentProfileRecord");
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Avalonia" or "CommunityToolkit.Mvvm"
                || reference.Name!.StartsWith("Avalonia", StringComparison.Ordinal));
        }
        else
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Sdk.Worker" or "Microsoft.Data.Sqlite"
                || reference.Name!.StartsWith("SQLitePCLRaw", StringComparison.Ordinal));
            Assert.Null(assembly.GetType("Sunder.Package.Agent.Memory.Semantic.MemoryLocalStore"));
        }
        AssertImportedRoleModel(assembly, "AgentDurableLifecycleEventEnvelope");
        Assert.Same(assembly, assembly.GetType(ImportedServices + "CYounics_Ai_Agent_Embedding_Provider.IEmbeddingProviderService", true)!.Assembly);
        Assert.Same(assembly, assembly.GetType(ImportedServices + "CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService", true)!.Assembly);
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Contracts.Contracts.IAgentEmbeddingProvider"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Contracts.Contracts.IAgentRuntimeCatalog"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Protocol.AgentRpcServices"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Protocol.AgentExecutionTargetRpc"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Memory.Semantic.Runtime.LegacySemanticMemoryRpcCatalog"));
    }
}
