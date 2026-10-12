using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    // The frozen retirement dispositions name the canonical implementation at
    // retirement. Follow only these reviewed ordinary-service successors when
    // checking today's source; never rewrite the captured disposition/history.
    private static readonly IReadOnlyDictionary<string, string[]> OrdinaryServiceSuccessors =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["src/Sunder.Package.Agent/Rpc/Owner/OwnerRuntimeService.cs"] =
                ["src/Sunder.Package.Agent/Runtime/AgentOwnerRuntimeProvider.cs"],
            ["src/Sunder.Package.Agent/Runtime/IAgentOwnerRuntimeProvider.cs"] =
                ["src/Sunder.Package.Agent/Runtime/AgentOwnerRuntimeProvider.cs"],
            ["src/Sunder.Package.Agent/App/Runtime/IAgentOwnerClient.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Owner/AppComposition.cs",
                 "src/Sunder.Package.Agent/Runtime/AgentAppRuntimeGateway.cs",
                 "src/Sunder.Package.Agent/Runtime/IAgentOwnerResources.cs"],
            ["src/Sunder.Package.Agent/Rpc/Owner/AgentOwnerRpcClient.cs"] =
                ["src/Sunder.Package.Agent/Runtime/AgentAppRuntimeGateway.cs",
                 "src/Sunder.Package.Agent/Runtime/AgentOwnerResources.cs"],
            ["src/Sunder.Package.Agent/Rpc/Owner/AgentRuntimeOperations.cs"] =
                ["src/Sunder.Package.Agent/Runtime/AgentOwnerRuntimeProvider.cs",
                 "src/Sunder.Package.Agent/Runtime/AgentRuntimeContracts.cs"],
            ["src/Sunder.Package.Agent.Provider.Anthropic/Rpc/CredentialClient.cs"] =
                ["src/Sunder.Package.Agent.Provider.Anthropic/Rpc/CredentialSettings.cs",
                 "src/Sunder.Package.Agent.Provider.Anthropic/App/AnthropicSettingsViewModel.cs"],
            ["src/Sunder.Package.Agent.Provider.Gemini/Rpc/CredentialClient.cs"] =
                ["src/Sunder.Package.Agent.Provider.Gemini/Rpc/CredentialSettings.cs",
                 "src/Sunder.Package.Agent.Provider.Gemini/App/GeminiSettingsViewModel.cs"],
            ["src/Sunder.Package.Agent.Provider.LMStudio/Rpc/CredentialClient.cs"] =
                ["src/Sunder.Package.Agent.Provider.LMStudio/Rpc/CredentialSettings.cs",
                  "src/Sunder.Package.Agent.Provider.LMStudio/App/LMStudioSettingsViewModel.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentEmbeddingBindings.cs"] =
                ["src/Sunder.Package.Agent/Services/AgentProfileService.cs",
                 "src/Sunder.Package.Agent/HistorySearch/HistoryEmbeddingProviderCatalog.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/GeneratedEmbeddingProviderClient.cs"] =
                ["src/Sunder.Package.Agent/HistorySearch/HistoryEmbeddingProviderCatalog.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentToolProfileBindings.cs"] =
                ["src/Sunder.Package.Agent/Services/AgentToolService.Invocation.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/GeneratedToolProfileCatalogClient.cs"] =
                ["src/Sunder.Package.Agent/Services/AgentToolService.Invocation.cs"],
            ["src/Sunder.Package.Agent/Runtime/IAgentToolProfileCatalog.cs"] =
                ["src/Sunder.Package.Agent/Services/AgentToolService.Invocation.cs"],
            // Retired constructor-only composition partials are now supplied by the
            // attributed services that own each provider implementation.
            ["src/Sunder.Package.Agent/Rpc/Owner/ProviderComposition.cs"] =
                ["src/Sunder.Package.Agent/Services/AgentProfileStackContributor.cs",
                 "src/Sunder.Package.Agent/Services/AgentWorkspaceStackContributor.cs",
                 "src/Sunder.Package.Agent/Services/BehaviorLoops/DefaultAgentBehaviorLoop.cs",
                 "src/Sunder.Package.Agent/Services/WorkspaceDocumentationContextService.cs",
                 "src/Sunder.Package.Agent/Services/AgentAttachmentService.cs",
                 "src/Sunder.Package.Agent/HistorySearch/HistorySearchIndexingService.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentWorkspaceEditorBindings.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/WorkspaceEditor.cs",
                 "src/Sunder.Package.Agent/PackageViews/AgentWorkspaceEditorInvocation.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentBehaviorBindings.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/BehaviorLoop.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/RunControl.cs",
                 "src/Sunder.Package.Agent/Services/AgentRunControlService.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentChatBindings.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/ChatProvider.cs",
                 "src/Sunder.Package.Agent/Services/AgentModelChatClient.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentCleanupBindings.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/SessionCleaner.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/DurableLifecycleObserver.cs",
                 "src/Sunder.Package.Agent/Services/AgentSessionCleanupDispatcher.cs",
                 "src/Sunder.Package.Agent/Services/AgentLifecycleDispatcher.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentExecutionBindings.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs",
                 "src/Sunder.Package.Agent/Services/AgentExecutionTargetService.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentMetadataBindings.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/SystemPromptContributor.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/PermissionSurface.cs",
                 "src/Sunder.Package.Agent/Services/AgentSystemPromptComposer.cs",
                 "src/Sunder.Package.Agent/Services/AgentPermissionService.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentObservedToolBindings.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/ToolExecutionObserved.cs",
                 "src/Sunder.Package.Agent/Services/AgentToolService.Invocation.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentPromptContextBindings.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/PromptContextContributor.cs",
                 "src/Sunder.Package.Agent/Services/AgentMemoryCoordinator.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentSelectableBindings.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/SelectableCapabilityProvider.cs",
                 "src/Sunder.Package.Agent/Services/AgentProfileService.Observation.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentToolBindings.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/ToolSource.cs",
                 "src/Sunder.Package.Agent/Services/AgentToolService.Catalog.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentToolProfileBindings.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/ToolSource.cs",
                 "src/Sunder.Package.Agent/Services/AgentToolService.Invocation.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentWorkspacePathBindings.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/WorkspacePathMigrator.cs",
                 "src/Sunder.Package.Agent/Services/AgentWorkspaceService.cs"],
            ["src/Sunder.Package.Agent.Builder/Rpc/BuilderRuntimeService.cs"] =
                ["src/Sunder.Package.Agent.Builder/Runtime/BuilderRuntimeService.cs"],
            ["src/Sunder.Package.Agent.Builder/Rpc/AppComposition.cs"] =
                ["src/Sunder.Package.Agent.Builder/BuilderProjectApplicationService.cs"],
            ["src/Sunder.Package.Agent.Builder/Rpc/BuilderRpcGateway.cs"] =
                ["src/Sunder.Package.Agent.Builder/Runtime/BuilderRuntimeService.cs"],
            ["src/Sunder.Package.Agent/Models/Extensions/AgentProfileSelectableCapabilityChangeObserver.cs"] =
                ["src/Sunder.Package.Agent/Services/AgentProfileService.Observation.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentRunControlRegistry.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/RunControl.cs",
                 "src/Sunder.Package.Agent/Services/AgentRunControlService.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentChatWireMapper.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/ChatProvider.cs",
                 "src/Sunder.Package.Agent/Services/AgentChatMapper.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentChatFailureProjection.cs"] =
                ["src/Sunder.Package.Agent/Services/AgentChatFailureProjection.cs"],
            ["src/Sunder.Package.Agent/Rpc/Consumers/AgentRpcContractIds.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/BehaviorLoop.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/ChatProvider.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/ChildRunExecutor.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/DurableLifecycleObserver.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/EmbeddingProvider.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/PermissionSurface.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/ProfileCapabilityConsumer.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/PromptContextContributor.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/RunControl.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/RuntimeCatalog.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/SelectableCapabilityProvider.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/SessionCleaner.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/SystemPromptContributor.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/ToolExecutionObserved.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/ToolSource.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/WorkspaceEditor.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/WorkspaceExecutionResolver.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/WorkspacePathMigrator.cs"],
            // Clean unpublished 2.0 contracts use native primitive projections; the
            // retired transport wrappers have no replacement type of their own.
            ["src/Sunder.Package.Agent/Rpc/Public/AgentRpcValues.cs"] =
                ["src/Sunder.Package.Agent/Models/Extensions/AgentServiceDescriptors.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/RuntimeCatalog.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs"],
            ["src/Sunder.Package.Agent/Rpc/Public/ToolModels.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/ToolSource.cs",
                 "src/Sunder.Package.Agent/Rpc/Public/ToolExecutionObserved.cs",
                 "src/Sunder.Package.Agent/Models/Tools/AgentToolCallRequest.cs",
                 "src/Sunder.Package.Agent/Models/Tools/AgentToolResult.cs"],
            ["src/Sunder.Package.Agent/Rpc/Public/ExecutionOperationModels.cs"] =
                ["src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs",
                 "src/Sunder.Package.Agent/Models/Execution/AgentExecutionTargetContext.cs",
                 "src/Sunder.Package.Agent/Models/Execution/AgentExecutionTargetDescriptor.cs"],
            ["src/Sunder.Package.Agent.Execution.Docker/Rpc/ManagementService.cs"] =
                ["src/Sunder.Package.Agent.Execution.Docker/DockerExecutionPresentationService.cs",
                 "src/Sunder.Package.Agent.Execution.Docker/Runtime/DockerPresentationContent.cs"],
            ["src/Sunder.Package.Agent.Execution.Docker/Rpc/ProviderComposition.cs"] =
                ["src/Sunder.Package.Agent.Execution.Docker/DockerExecutionPresentationService.cs",
                 "src/Sunder.Package.Agent.Execution.Docker/DockerExecutionWorkspaceConfigService.cs"],
            ["src/Sunder.Package.Agent.Execution.Local/Rpc/ManagementService.cs"] =
                ["src/Sunder.Package.Agent.Execution.Local/LocalExecutionPresentationService.cs",
                 "src/Sunder.Package.Agent.Execution.Local/Models/LocalExecutionPresentationModels.cs"],
            ["src/Sunder.Package.Agent.Execution.Local/Rpc/ProviderComposition.cs"] =
                ["src/Sunder.Package.Agent.Execution.Local/LocalExecutionTarget.cs",
                 "src/Sunder.Package.Agent.Execution.Local/LocalExecutionPresentationService.cs",
                 "src/Sunder.Package.Agent.Execution.Local/LocalExecutionWorkspaceConfigService.cs"],
            ["src/Sunder.Package.Agent.Execution.Local/Rpc/AppComposition.cs"] =
                ["src/Sunder.Package.Agent.Execution.Local/App/LocalExecutionSettingsViewModel.cs",
                 "src/Sunder.Package.Agent.Execution.Local/LocalExecutionPresentationService.cs"],
            ["src/Sunder.Package.Agent.Mcp/Rpc/ManagementService.cs"] =
                ["src/Sunder.Package.Agent.Mcp/Runtime/McpManagementService.cs"],
            ["src/Sunder.Package.Agent.Execution.Docker/Rpc/ManagementClient.cs"] =
                ["src/Sunder.Package.Agent.Execution.Docker/DockerExecutionPresentationService.cs"],
            ["src/Sunder.Package.Agent.Execution.Local/Rpc/ManagementClient.cs"] =
                ["src/Sunder.Package.Agent.Execution.Local/LocalExecutionPresentationService.cs"],
            ["src/Sunder.Package.Agent.Mcp/Rpc/ProviderComposition.cs"] =
                ["src/Sunder.Package.Agent.Mcp/Runtime/McpManagementService.cs"],
            ["src/Sunder.Package.Agent.Mcp/Rpc/RuntimeWorkspaceCatalog.cs"] =
                ["src/Sunder.Package.Agent.Mcp/Runtime/McpManagementService.cs"],
            ["src/Sunder.Package.Agent.Mcp/Rpc/McpManagementRpcProvider.cs"] =
                ["src/Sunder.Package.Agent.Mcp/Runtime/McpManagementService.cs"],
            ["src/Sunder.Package.Agent.Mcp/Rpc/ManagementClient.cs"] =
                ["src/Sunder.Package.Agent.Mcp/Runtime/McpManagementService.cs"],
            ["src/Sunder.Package.Agent.Memory.Semantic/Rpc/ManagementService.cs"] =
                ["src/Sunder.Package.Agent.Memory.Semantic/MemoryInspectorService.Service.cs"],
            ["src/Sunder.Package.Agent.Memory.Semantic/Rpc/ManagementModels.cs"] =
                ["src/Sunder.Package.Agent.Memory.Semantic/Runtime/ProjectionContent.cs"],
            ["src/Sunder.Package.Agent.Memory.Semantic/Rpc/SemanticMemoryRpcCatalog.cs"] =
                ["src/Sunder.Package.Agent.Memory.Semantic/Runtime/MemoryChangeQueue.cs"],
            ["src/Sunder.Package.Agent.Provider.Anthropic/Rpc/ChatProvider.cs"] =
                ["src/Sunder.Package.Agent.Provider.Anthropic/AnthropicAgentProvider.cs"],
            ["src/Sunder.Package.Agent.Provider.Gemini/Rpc/ChatProvider.cs"] =
                ["src/Sunder.Package.Agent.Provider.Gemini/GeminiAgentProvider.cs"],
            ["src/Sunder.Package.Agent.Provider.LMStudio/Rpc/ChatProvider.cs"] =
                ["src/Sunder.Package.Agent.Provider.LMStudio/LMStudioAgentProvider.cs"],
            ["src/Sunder.Package.Agent.Provider.OpenAI/Rpc/ManagementService.cs"] =
                ["src/Sunder.Package.Agent.Provider.OpenAI/Management/OpenAiManagementService.cs"],
            ["src/Sunder.Package.Agent.Skills/Rpc/ManagementService.cs"] =
                ["src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeGateway.cs"],
            ["src/Sunder.Package.Agent.Skills/Rpc/SkillsRuntimeBindings.cs"] =
                ["src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeGateway.cs"],
            ["src/Sunder.Package.Agent.Subagents/Rpc/ManagementService.cs"] =
                ["src/Sunder.Package.Agent.Subagents/Runtime/SubagentManagementService.cs"],
            ["src/Sunder.Package.Agent.Subagents/Rpc/SubagentRuntimeBindings.cs"] =
                ["src/Sunder.Package.Agent.Subagents/Runtime/SubagentManagementService.cs"],
            ["src/Sunder.Package.Agent.Subagents/Runtime/SubsessionLocalRuntimeAdapter.cs"] =
                ["src/Sunder.Package.Agent.Subagents/Runtime/SubsessionTranscriptReader.cs"],
            ["src/Sunder.Package.Agent.Tools.Files/Rpc/ToolSourceProvider.cs"] =
                ["src/Sunder.Package.Agent.Tools.Files/FilesToolSource.cs"],
            ["src/Sunder.Package.Agent.Tools.Files/Rpc/FilesExecutionFacets.cs"] =
                ["src/Sunder.Package.Agent.Tools.Files/Runtime/FilesExecutionFacets.cs"],
            ["src/Sunder.Package.Agent.Tools.Shell/Rpc/ToolSourceProvider.cs"] =
                ["src/Sunder.Package.Agent.Tools.Shell/ShellToolSource.cs"],
            ["src/Sunder.Package.Agent.Tools.Web/Rpc/ToolSourceProvider.cs"] =
                ["src/Sunder.Package.Agent.Tools.Web/Services/WebToolSource.cs"],
            ["src/Sunder.Package.Agent.Mcp/Runtime/McpManagementRpcProvider.cs"] =
                ["src/Sunder.Package.Agent.Mcp/Runtime/McpManagementService.cs"],
            ["src/Sunder.Package.Agent.Memory.Semantic/Rpc/MemoryAppRuntimeGateway.Transport.cs"] =
                ["src/Sunder.Package.Agent.Memory.Semantic/MemoryInspectorService.Service.cs"],
            ["src/Sunder.Package.Agent.Memory.Semantic/Rpc/ProviderComposition.cs"] =
                ["src/Sunder.Package.Agent.Memory.Semantic/MemoryInspectorService.Service.cs"],
            ["src/Sunder.Package.Agent.Memory.Semantic/Rpc/ManagementClient.cs"] =
                ["src/Sunder.Package.Agent.Memory.Semantic/MemoryInspectorService.Service.cs"],
            ["src/Sunder.Package.Agent.Provider.Anthropic/Rpc/CredentialSettings.Composition.cs"] =
                ["src/Sunder.Package.Agent.Provider.Anthropic/Rpc/CredentialSettings.cs"],
            ["src/Sunder.Package.Agent.Provider.Gemini/Rpc/CredentialSettings.Composition.cs"] =
                ["src/Sunder.Package.Agent.Provider.Gemini/Rpc/CredentialSettings.cs"],
            ["src/Sunder.Package.Agent.Provider.LMStudio/Rpc/CredentialSettings.Composition.cs"] =
                ["src/Sunder.Package.Agent.Provider.LMStudio/Rpc/CredentialSettings.cs"],
            ["src/Sunder.Package.Agent.Provider.OpenAI/Rpc/ProviderComposition.cs"] =
                ["src/Sunder.Package.Agent.Provider.OpenAI/Management/OpenAiManagementService.cs"],
            ["src/Sunder.Package.Agent.Provider.OpenAI/Rpc/AppComposition.cs"] =
                ["src/Sunder.Package.Agent.Provider.OpenAI/OpenAiSettingsViewModel.cs",
                 "src/Sunder.Package.Agent.Provider.OpenAI/OpenAiSettingsView.axaml.cs"],
            ["src/Sunder.Package.Agent.Skills/Rpc/ProviderComposition.cs"] =
                ["src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeGateway.cs"],
            ["src/Sunder.Package.Agent.Subagents/Rpc/ProviderComposition.cs"] =
                ["src/Sunder.Package.Agent.Subagents/Runtime/SubagentManagementService.cs"],
            ["src/Sunder.Package.Agent.Tools.Files/Rpc/FilesExecutionBindings.cs"] =
                ["src/Sunder.Package.Agent.Tools.Files/Runtime/FileExecutionTarget.cs"],
            ["src/Sunder.Package.Agent.Tools.Shell/Rpc/ShellExecutionBindings.cs"] =
                ["src/Sunder.Package.Agent.Tools.Shell/ShellToolSource.cs"],
        };

    private static IEnumerable<string> CurrentRetirementReplacements(string path)
        => (OrdinaryServiceSuccessors.TryGetValue(path, out var current) ? current : [path])
            .Select(CurrentOwnedPublicReplacement).Distinct(StringComparer.Ordinal);

    private static readonly Lazy<IReadOnlyDictionary<string, OrdinarySourceDelta>> OrdinarySourceHistory = new(() =>
    {
        var text = ReadNormalizedProjectText(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "tests/Sunder.Package.Agent.Tests/Fixtures/OrdinaryServices/source-history.json"));
        AssertProjectHash("0340cfce10536c141ea73875fd513631d2903c301a45afca678a7cc26e0b105c", text, "Ordinary source history");
        var history = JsonSerializer.Deserialize<OrdinarySourceHistoryRecord>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, history.SchemaVersion);
        Assert.Equal(6, history.Sources.Length);
        return history.Sources.ToDictionary(source => source.Path, StringComparer.Ordinal);
    });

    internal static bool HasOrdinarySourceHistory(string relative) => OrdinarySourceHistory.Value.ContainsKey(relative);

    internal static string BeforeOrdinaryServices(string root, string path, string current)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        current = BeforeManagedConsumerServices(relative, current);
        if (!OrdinarySourceHistory.Value.TryGetValue(relative, out var source)) return current;
        AssertProjectHash(source.CurrentSha256, current, relative + " (reviewed ordinary source)");
        var split = current.Split('\n');
        var lines = split.Select((line, index) => index < split.Length - 1 ? line + "\n" : line).ToList();
        foreach (var edit in source.Edits.Reverse())
        {
            Assert.InRange(edit.StartLine, 0, lines.Count);
            Assert.InRange(edit.RemoveLineCount, 0, lines.Count - edit.StartLine);
            lines.RemoveRange(edit.StartLine, edit.RemoveLineCount);
            lines.InsertRange(edit.StartLine, edit.BeforeLines);
        }
        var before = string.Concat(lines);
        AssertProjectHash(source.BeforeSha256, before, relative + " (authenticated before-image)");
        return before;
    }

    private sealed record OrdinarySourceHistoryRecord(int SchemaVersion, OrdinarySourceDelta[] Sources);
    private sealed record OrdinarySourceDelta(string Path, string BeforeSha256, string CurrentSha256, OrdinarySourceEdit[] Edits);
    private sealed record OrdinarySourceEdit(int StartLine, int RemoveLineCount, string[] BeforeLines);
}
