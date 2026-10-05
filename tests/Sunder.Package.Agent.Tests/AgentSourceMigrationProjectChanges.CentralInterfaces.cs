using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    // Keep the 142 original business/catalog moves distinct from these subsequently
    // reviewed interface moves. Never discover approval from a wildcard or filename.
    internal static IReadOnlyDictionary<string, string> CentralInterfaceRelocations => CentralInterfaceHistory.Relocations;
    internal static IEnumerable<KeyValuePair<string, string>> ReviewedCentralRelocations
        => CentralSemanticRelocations.Concat(CentralInterfaceRelocations);

    internal static string BeforeCentralInterfaceRelocation(string path, string text)
    {
        var normalized = path.Replace('\\', '/');
        var move = CentralInterfaceHistory.Moves.SingleOrDefault(move => normalized.EndsWith("/src/Sunder.Package.Agent/" + move.Current, StringComparison.Ordinal));
        if (move is null) return text;
        const string role = "[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]\n";
        Assert.Equal(move.SharedRoles + 1, text.Split(role, StringSplitOptions.None).Length);
        return text.Replace(role, "", StringComparison.Ordinal);
    }

    private static class CentralInterfaceHistory
    {
        internal static readonly CentralInterfaceMove[] Moves =
        [
            new("Execution/IAgentExecutionPathEnvironment.cs", "Models/Execution/IAgentExecutionPathEnvironment.cs"),
            new("Execution/IAgentExecutionPathMapper.cs", "Models/Execution/IAgentExecutionPathMapper.cs"),
            new("Execution/IAgentExecutionResourceResolver.cs", "Models/Execution/IAgentExecutionResourceResolver.cs"),
            new("Execution/IAgentExecutionScopeProvider.cs", "Models/Execution/IAgentExecutionScopeProvider.cs"),
            new("Execution/IAgentExecutionTarget.cs", "Models/Execution/IAgentExecutionTarget.cs", 1),
            new("Execution/IAgentProcessExecutionTarget.cs", "Models/Execution/IAgentProcessExecutionTarget.cs"),
            new("Execution/IAgentRangedFileExecutionTarget.cs", "Models/Execution/IAgentRangedFileExecutionTarget.cs"),
            new("Execution/IAgentResourceAuthorityExecutionTarget.cs", "Models/Execution/IAgentResourceAuthorityExecutionTarget.cs"),
            new("Execution/IAgentScopedInstructionDiscoveryTarget.cs", "Models/Execution/IAgentScopedInstructionDiscoveryTarget.cs"),
            new("Execution/IAgentStructuredFileSearchExecutionTarget.cs", "Models/Execution/IAgentStructuredFileSearchExecutionTarget.cs"),
            new("Execution/IAgentWorkspaceEditorContributor.cs", "Models/Execution/IAgentWorkspaceEditorContributor.cs"),
            new("Execution/IAgentWorkspaceExecutionResolver.cs", "Models/Execution/IAgentWorkspaceExecutionResolver.cs"),
            new("Execution/IAgentWorkspacePathMigrationContributor.cs", "Models/Execution/IAgentWorkspacePathMigrationContributor.cs"),
            new("Extensions/IAgentDurableLifecycleObserver.cs", "Models/Extensions/IAgentDurableLifecycleObserver.cs"),
            new("Extensions/IAgentProfileCapabilityConsumer.cs", "Models/Extensions/IAgentProfileCapabilityConsumer.cs"),
            new("Extensions/IAgentProfileSelectableCapabilityChangeNotifier.cs", "Models/Extensions/IAgentProfileSelectableCapabilityChangeNotifier.cs"),
            new("Extensions/IAgentProfileSelectableCapabilityProvider.cs", "Models/Extensions/IAgentProfileSelectableCapabilityProvider.cs"),
            new("Extensions/IAgentPromptContextAcknowledgmentSink.cs", "Models/Extensions/IAgentPromptContextAcknowledgmentSink.cs"),
            new("Extensions/IAgentPromptContextContributor.cs", "Models/Extensions/IAgentPromptContextContributor.cs"),
            new("Extensions/IAgentSystemPromptContributor.cs", "Models/Extensions/IAgentSystemPromptContributor.cs"),
            new("Providers/IAgentChatProvider.cs", "Models/Providers/IAgentChatProvider.cs"),
            new("Providers/IAgentEmbeddingProvider.cs", "Models/Providers/IAgentEmbeddingProvider.cs"),
            new("Providers/IAgentEmbeddingSpaceIdentityProvider.cs", "Models/Providers/IAgentEmbeddingSpaceIdentityProvider.cs"),
            new("Providers/IAgentUtilityModelProvider.cs", "Models/Providers/IAgentUtilityModelProvider.cs"),
            new("Runtime/AgentBehaviorLoopRuntimeCapabilities.cs", "Runtime/AgentBehaviorLoopRuntimeCapabilities.cs"),
            new("Runtime/IAgentBehaviorLoop.cs", "Runtime/IAgentBehaviorLoop.cs"),
            new("Runtime/IAgentBehaviorLoopRuntime.cs", "Runtime/IAgentBehaviorLoopRuntime.cs"),
            new("Runtime/IAgentChildRunExecutor.cs", "Runtime/IAgentChildRunExecutor.cs"),
            new("Runtime/IAgentInnerBehaviorLoopRuntime.cs", "Runtime/IAgentInnerBehaviorLoopRuntime.cs"),
            new("Runtime/IAgentRuntimeCatalog.cs", "Runtime/IAgentRuntimeCatalog.cs"),
            new("Runtime/IAgentSessionDataCleaner.cs", "Runtime/IAgentSessionDataCleaner.cs"),
            new("Runtime/IAgentTranscriptCatalog.cs", "Runtime/IAgentTranscriptCatalog.cs"),
            new("Tools/IAgentNativeToolSource.cs", "Models/Tools/IAgentNativeToolSource.cs"),
            new("Tools/IAgentPermissionAwareToolSource.cs", "Models/Tools/IAgentPermissionAwareToolSource.cs", 1),
            new("Tools/IAgentPermissionSurface.cs", "Models/Tools/IAgentPermissionSurface.cs"),
            new("Tools/IAgentToolExecutionPreflightSource.cs", "Models/Tools/IAgentToolExecutionPreflightSource.cs", 1),
            new("Tools/IAgentToolPresentationResolver.cs", "Models/Tools/IAgentToolPresentationResolver.cs", 1),
            new("Tools/IAgentToolSource.cs", "Models/Tools/IAgentToolSource.cs", 1),
        ];

        internal static readonly IReadOnlyDictionary<string, string> Relocations = ReadRelocations();

        private static IReadOnlyDictionary<string, string> ReadRelocations()
        {
            var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
            using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "compatibility/agent-rpc-migration-baseline.json")));
            var sources = oracle.RootElement.GetProperty("sourceRelocations").EnumerateArray()
                .ToDictionary(source => source.GetProperty("path").GetString()!, StringComparer.Ordinal);
            var relocations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var move in Moves)
            {
                var original = "src/Sunder.Package.Agent.Contracts/" + move.Original;
                var current = "src/Sunder.Package.Agent/" + move.Current;
                Assert.False(File.Exists(Path.Combine(root, original)), $"Duplicate central source remains: {original}");
                var path = Path.Combine(root, current);
                Assert.True(File.Exists(path), $"Reviewed central relocation is missing: {current}");
                var text = BeforeCentralInterfaceRelocation(path, ReadNormalizedProjectText(path));
                Assert.Equal(sources[original].GetProperty("sha256").GetString(), ProjectTextHash(text));
                Assert.True(relocations.TryAdd(original, current));
            }
            return relocations;
        }
    }

    private sealed record CentralInterfaceMove(string Original, string Current, int SharedRoles = 0);
}
