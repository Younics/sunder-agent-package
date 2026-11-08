using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyOwnerAppRole(string worker)
    {
        var load = new AssemblyLoadContext("agent-role-local-presentation", isCollectible: true);
        try
        {
            var workerOutput = SingleProjectRoleOutput(worker, "worker");
            var appOutput = SingleProjectRoleOutput(worker, "app");
            var runtime = Load(Path.Combine(workerOutput, "Sunder.Package.Agent.dll"));
            var presentation = Load(Path.Combine(appOutput, "Sunder.Package.Agent.App.dll"));
            Assert.DoesNotContain(Directory.EnumerateFiles(appOutput, "*", SearchOption.AllDirectories),
                path => Path.GetFileName(path).Contains("sqlite", StringComparison.OrdinalIgnoreCase));
            foreach (var type in new[] { "Storage.AgentLocalStore", "Services.AgentRunCoordinator", "HistorySearch.HistorySearchIndexingService", "Services.BehaviorLoops.DefaultAgentBehaviorLoop" })
                Assert.Null(presentation.GetType("Sunder.Package.Agent." + type));
            AgentCanonicalSourceMigrationTests.AssertRoleLocalOwnerModels(runtime);
            AgentAppSourceMigrationTests.AssertRoleLocalModels(presentation);
            foreach (var name in new[] { "Sunder.Package.Agent.Runtime.AgentDashboardProjection", "Sunder.Package.Agent.Runtime.AgentTransportProfile",
                "Sunder.Package.Agent.Services.AgentToolCatalogEntry", "Sunder.Package.Agent.HistorySearch.HistorySearchRequest" })
            {
                Assert.Same(runtime, runtime.GetType(name, true)!.Assembly);
                Assert.NotSame(runtime.GetType(name), presentation.GetType(name, true));
            }
            Assert.False(File.Exists(Path.Combine(workerOutput, "Sunder.Package.Agent.OwnerProtocol.dll")));
            Assert.DoesNotContain("Sunder.Package.Agent.OwnerProtocol", File.ReadAllText(Path.Combine(workerOutput, "Sunder.Package.Agent.deps.json")), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(workerOutput, "Sunder.Package.Agent.Protocol.dll")));
            Assert.DoesNotContain("Sunder.Package.Agent.Protocol", File.ReadAllText(Path.Combine(workerOutput, "Sunder.Package.Agent.deps.json")), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(appOutput, "Sunder.Package.Agent.OwnerProtocol.dll")));
            Assert.DoesNotContain("Sunder.Package.Agent.OwnerProtocol", File.ReadAllText(Path.Combine(appOutput, "Sunder.Package.Agent.App.deps.json")), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(appOutput, "Sunder.Package.Agent.Protocol.dll")));
            Assert.DoesNotContain("Sunder.Package.Agent.Protocol", File.ReadAllText(Path.Combine(appOutput, "Sunder.Package.Agent.App.deps.json")), StringComparison.Ordinal);
            using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(worker, "obj/Debug/net10.0/sunder-analysis", RuntimeInformation.RuntimeIdentifier, "sunder-rpc/authoring-receipt.v1.json")));
            var descriptors = receipt.RootElement.GetProperty("contracts").EnumerateArray().ToDictionary(
                contract => contract.GetProperty("contractId").GetString()!,
                contract => SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(contract.GetProperty("canonicalDescriptor").GetString()!)));
            await AgentCanonicalSourceMigrationTests.ExerciseComposition(runtime, presentation, descriptors);
            await VerifyAgentToolConsumers(runtime, presentation);
            await VerifyAgentWorkspaceEditorConsumer(presentation);
        }
        finally { load.Unload(); }

        Assembly Load(string path)
        {
            using var content = File.OpenRead(path);
            return load.LoadFromStream(content);
        }
    }
}
