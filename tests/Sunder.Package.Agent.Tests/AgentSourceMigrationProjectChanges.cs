using Xunit;

namespace Sunder.Package.Agent.Tests;

// Explicit additions needed by source migration, reconciled against the immutable
// pre-migration project hashes. No original project content is exempted from checks.
internal static partial class AgentSourceMigrationProjectChanges
{
    private const string FailureBoundaryInclude = "../Sunder.Package.Agent.Tests/RegressionTestRpcState.FailureBoundary.cs";
    private static readonly IReadOnlyDictionary<string, string> Additions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["src/Sunder.Package.Agent/Sunder.Package.Agent.csproj"] =
            "    <Compile Remove=\"Rpc\\Public\\AgentRpcValues.cs;Rpc\\Public\\WorkspaceModels.cs;Rpc\\Public\\WorkspaceExecutionResolver.cs;Rpc\\Public\\WorkspacePathMigrator.cs;Rpc\\Public\\ExecutionTarget.cs;Rpc\\Public\\ExecutionContextModels.cs;Rpc\\Public\\ExecutionOperationModels.cs;Rpc\\Public\\ExecutionResourceModels.cs;Rpc\\Public\\WorkspaceEditor.cs;Rpc\\Public\\PermissionSurface.cs;Rpc\\Public\\ProfileCapabilityConsumer.cs;Rpc\\Public\\SelectableCapabilityProvider.cs;Rpc\\Public\\EmbeddingProvider.cs;Rpc\\Public\\ToolSource.cs;Rpc\\Public\\ToolModels.cs;Rpc\\Public\\ToolExecutionObserved.cs;Rpc\\Public\\ConversationModels.cs;Rpc\\Public\\DurableLifecycleObserver.cs;Rpc\\Public\\SystemPromptContributor.cs;Rpc\\Public\\ChatProvider.cs;Rpc\\Public\\ChatValueModels.cs;Rpc\\Public\\ChatMessageModels.cs;Rpc\\Public\\SessionCleaner.cs;Rpc\\Public\\RuntimeCatalog.cs;Rpc\\Public\\RuntimeCatalogModels.cs;Rpc\\Public\\RuntimeCatalogTranscriptModels.cs;Rpc\\Public\\RuntimeCatalogMapping.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\Public\\RunControl.cs;Rpc\\Public\\RunControl.Chat.cs;Rpc\\Public\\RunControlModels.cs;Rpc\\Public\\RunControlChatModels.cs;Rpc\\Public\\PromptContextModels.cs;Rpc\\Public\\BehaviorLoopModels.cs;Rpc\\Public\\RunControlMapping.cs;Rpc\\Public\\RunControlMapping.Context.cs;Rpc\\Public\\RunControlMapping.Tools.cs;Rpc\\Public\\RunControlMapping.Chat.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\Public\\BehaviorLoop.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\Public\\ChildRunExecutor.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\Public\\PromptContextContributor.cs;Rpc\\Public\\PromptContextRequestModels.cs;Rpc\\Public\\PromptContextMapping.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\Owner\\OwnerValues.cs;Rpc\\Owner\\OwnerWorkspaceModels.cs;Rpc\\Owner\\OwnerTranscriptModels.cs;Rpc\\Owner\\OwnerCatalogModels.cs;Rpc\\Owner\\OwnerPermissionModels.cs;Rpc\\Owner\\OwnerCommandModels.cs;Rpc\\Owner\\OwnerProjectionModels.cs;Rpc\\Owner\\OwnerAttachmentModels.cs;Rpc\\Owner\\OwnerHistoryModels.cs;Rpc\\Owner\\OwnerValueMapper.cs;Rpc\\Owner\\OwnerRuntimeService.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\Owner\\OwnerRuntimeGateway.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\Owner\\AgentStackProviders.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\Owner\\ProviderComposition.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\Owner\\AppComposition.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\Owner\\AgentOwnerRpcClient.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\Owner\\AgentRuntimeOperations.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\Consumers\\**\\*.cs\" />\n"
            + "    <Compile Remove=\"Runtime\\IAgentOwnerRuntimeProvider.cs\" />\n"
            + "    <Compile Remove=\"Services\\AgentExecutionTargetWarmupResult.cs;Services\\AgentToolCatalogEntry.cs;Services\\AgentWorkspaceBindingRoles.cs;Runtime\\AgentTranscriptPageCursor.cs;Runtime\\AgentWorkspaceDefaults.cs;Runtime\\AgentOwnerOperationException.cs\" />\n\0"
            + "  <Import Project=\"Models\\AgentSemanticSources.props\" />\n\0"
            + "    <Compile Remove=\"@(SunderAgentSemanticSource)\" />\n",
        ["src/Sunder.Package.Agent.Contracts/Sunder.Package.Agent.Contracts.csproj"] =
            "  <Import Project=\"..\\Sunder.Package.Agent\\Models\\AgentSemanticSources.props\" />\n\n\0"
            + "    <Compile Include=\"@(SunderAgentSemanticSource)\" />\n"
            + "    <Compile Include=\"..\\Sunder.Package.Agent.Provider.Shared\\Models\\ProviderChatModels.cs\" Link=\"Rpc\\ProviderChatModels.cs\" />\n"
            + "    <Compile Include=\"..\\Sunder.Package.Agent.Shared\\Execution\\AgentExecutionFacetIds.cs\" Link=\"Rpc\\AgentExecutionFacetIds.cs\" />\n"
            + "    <Compile Include=\"..\\Sunder.Package.Agent\\Rpc\\Consumers\\AgentRpcContractIds.cs\" Link=\"Rpc\\AgentRpcContractIds.cs\" />\n"
            + "    <Compile Include=\"..\\Sunder.Package.Agent\\Rpc\\Consumers\\AgentRunControlRegistry.cs\" Link=\"Rpc\\AgentRunControlRegistry.cs\" />\n"
            + "    <Compile Include=\"..\\Sunder.Package.Agent\\Rpc\\Consumers\\AgentChatWireMapper.cs\" Link=\"Rpc\\AgentChatWireMapper.cs\" />\n"
            + "    <Compile Include=\"..\\Sunder.Package.Agent\\Rpc\\Consumers\\AgentChatFailureProjection.cs\" Link=\"Rpc\\AgentChatFailureProjection.cs\" />\n",
        ["src/Sunder.Package.Agent/App/Sunder.Package.Agent.App.csproj"] =
            "    <Compile Remove=\"Runtime\\AgentRuntimeOperations.Legacy.cs\" />\n\0"
            + "    <Compile Include=\"..\\Runtime\\Rpc\\AgentToolBindings.Legacy.cs\" Link=\"Runtime\\Rpc\\AgentToolBindings.Legacy.cs\" />\n",
        ["src/Sunder.Package.Agent/OwnerProtocol/Sunder.Package.Agent.OwnerProtocol.csproj"] =
            "    <Compile Include=\"..\\Services\\AgentExecutionTargetWarmupResult.cs\" Link=\"Services\\AgentExecutionTargetWarmupResult.cs\" />\n"
            + "    <Compile Include=\"..\\Services\\AgentToolCatalogEntry.cs\" Link=\"Services\\AgentToolCatalogEntry.cs\" />\n"
            + "    <Compile Include=\"..\\Services\\AgentWorkspaceBindingRoles.cs\" Link=\"Services\\AgentWorkspaceBindingRoles.cs\" />\n"
            + "    <Compile Include=\"..\\Runtime\\AgentTranscriptPageCursor.cs\" Link=\"Runtime\\AgentTranscriptPageCursor.cs\" />\n"
            + "    <Compile Include=\"..\\Runtime\\AgentWorkspaceDefaults.cs\" Link=\"Runtime\\AgentWorkspaceDefaults.cs\" />\n"
            + "    <Compile Include=\"..\\App\\Runtime\\AgentRuntimeOperations.Legacy.cs\" Link=\"Runtime\\AgentRuntimeOperations.Legacy.cs\" />\n"
            + "    <Compile Include=\"..\\Runtime\\AgentOwnerOperationException.cs\" Link=\"Runtime\\AgentOwnerOperationException.cs\" />\n"
            + "    <Compile Include=\"..\\Runtime\\IAgentOwnerRuntimeProvider.cs\" Link=\"Runtime\\IAgentOwnerRuntimeProvider.cs\" />\n",
        ["tests/Sunder.Package.Agent.Execution.Local.Tests/Sunder.Package.Agent.Execution.Local.Tests.csproj"] =
            "    <Compile Include=\"..\\Sunder.Package.Agent.Tests\\RegressionTestRpcState.FailureBoundary.cs\" Link=\"RegressionTestRpcState.FailureBoundary.cs\" />\n",
        ["src/Sunder.Package.Agent.Provider.Gemini/Sunder.Package.Agent.Provider.Gemini.csproj"] =
            "    <Compile Remove=\"Rpc\\CredentialSettings.cs;Rpc\\CredentialSettings.Composition.cs;Rpc\\CredentialClient.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\GeminiWorkerBindings.cs;Rpc\\GeminiProviderValues.cs;Rpc\\ChatProvider.cs;Rpc\\ChatStream.cs;Rpc\\ChatValues.cs;Rpc\\EmbeddingProvider.cs\" />\n\0"
            + "    <Compile Include=\"..\\Sunder.Package.Agent.Provider.Shared\\ProviderUtilityModelSelection.cs\" Link=\"Provider.Shared\\ProviderUtilityModelSelection.cs\" />\n",
        ["src/Sunder.Package.Agent.Provider.Gemini/App/Sunder.Package.Agent.Provider.Gemini.App.csproj"] =
            "    <Compile Include=\"..\\PackageDependency.Legacy.cs\" Link=\"PackageDependency.Legacy.cs\" />\n",
        ["src/Sunder.Package.Agent.Provider.Anthropic/Sunder.Package.Agent.Provider.Anthropic.csproj"] =
            "    <Compile Remove=\"Rpc\\CredentialSettings.cs;Rpc\\CredentialSettings.Composition.cs;Rpc\\CredentialClient.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\AnthropicWorkerBindings.cs;Rpc\\AnthropicProviderValues.cs;Rpc\\ChatProvider.cs;Rpc\\ChatStream.cs;Rpc\\ChatValues.cs\" />\n\0"
            + "    <Compile Include=\"..\\Sunder.Package.Agent.Provider.Shared\\ProviderUtilityModelSelection.cs\" Link=\"Provider.Shared\\ProviderUtilityModelSelection.cs\" />\n",
        ["src/Sunder.Package.Agent.Provider.Anthropic/App/Sunder.Package.Agent.Provider.Anthropic.App.csproj"] =
            "    <Compile Include=\"..\\PackageDependency.Legacy.cs\" Link=\"PackageDependency.Legacy.cs\" />\n",
        ["src/Sunder.Package.Agent.Provider.LMStudio/Sunder.Package.Agent.Provider.LMStudio.csproj"] =
            "    <Compile Remove=\"Rpc\\CredentialSettings.cs;Rpc\\CredentialSettings.Composition.cs;Rpc\\CredentialClient.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\LMStudioWorkerBindings.cs;Rpc\\LMStudioProviderValues.cs;Rpc\\ChatProvider.cs;Rpc\\ChatStream.cs;Rpc\\ChatValues.cs;Rpc\\EmbeddingProvider.cs\" />\n\0"
            + "    <Compile Include=\"..\\Sunder.Package.Agent.Provider.Shared\\ProviderUtilityModelSelection.cs\" Link=\"Provider.Shared\\ProviderUtilityModelSelection.cs\" />\n",
        ["src/Sunder.Package.Agent.Provider.LMStudio/App/Sunder.Package.Agent.Provider.LMStudio.App.csproj"] =
            "    <Compile Include=\"..\\PackageDependency.Legacy.cs\" Link=\"PackageDependency.Legacy.cs\" />\n",
        ["src/Sunder.Package.Agent.Mcp/Sunder.Package.Agent.Mcp.csproj"] =
            "    <Compile Include=\"Runtime\\McpRuntimeManagementClient.cs\" />\n"
            + "    <Compile Include=\"Runtime\\IMcpWorkspaceCatalog.cs\" />\n"
            + "    <Compile Include=\"Runtime\\LegacyMcpWorkspaceCatalog.cs\" />\n\0"
            + "    <Compile Include=\"PackageDependency.Legacy.cs\" />\n\0"
            + "    <Compile Remove=\"Services\\McpManagementServiceModels.cs;Services\\McpServerName.cs;Services\\McpOAuthConstants.cs\" />\n\0"
            + "    <Compile Include=\"Runtime\\McpManagementRpcProvider.Legacy.cs\" />\n",
        ["src/Sunder.Package.Agent.Mcp/Shared/Sunder.Package.Agent.Mcp.Shared.csproj"] =
            "    <Compile Include=\"..\\Runtime\\McpManagementModels.cs\" Link=\"McpManagementModels.cs\" />\n"
            + "    <Compile Include=\"..\\Runtime\\McpManagementPayload.cs\" Link=\"McpManagementPayload.cs\" />\n"
            + "    <Compile Include=\"..\\Runtime\\McpManagementContent.cs\" Link=\"McpManagementContent.cs\" />\n"
            + "    <Compile Include=\"..\\Services\\McpManagementServiceModels.cs\" Link=\"McpManagementServiceModels.cs\" />\n"
            + "    <Compile Include=\"..\\Services\\McpServerName.cs\" Link=\"McpServerName.cs\" />\n"
            + "    <Compile Include=\"..\\Services\\McpOAuthConstants.cs\" Link=\"McpOAuthConstants.cs\" />\n",
        ["src/Sunder.Package.Agent.Mcp/App/Sunder.Package.Agent.Mcp.App.csproj"] =
            "    <Compile Include=\"..\\PackageDependency.Legacy.cs\" Link=\"PackageDependency.Legacy.cs\" />\n\0"
            + "    <Compile Include=\"..\\Runtime\\McpOwnerManagementRpcClient.Legacy.cs\" Link=\"Runtime\\McpOwnerManagementRpcClient.Legacy.cs\" />\n",
        ["src/Sunder.Package.Agent.Provider.OpenAI/Sunder.Package.Agent.Provider.OpenAI.csproj"] =
            "    <Compile Include=\"Management\\OpenAiRuntimeManagementClient.cs\" />\n\0"
            + "    <Compile Include=\"PackageDependency.Legacy.cs\" />\n\0"
            + "    <Compile Include=\"Management\\OpenAiAuthOperationHandler.cs\" />\n",
        ["src/Sunder.Package.Agent.Provider.OpenAI/Shared/Sunder.Package.Agent.Provider.OpenAI.Shared.csproj"] =
            "    <Compile Include=\"..\\Management\\OpenAiManagementModels.cs\" Link=\"OpenAiManagementModels.cs\" />\n"
            + "    <Compile Include=\"..\\Management\\OpenAiUtilityModelSelection.cs\" Link=\"OpenAiUtilityModelSelection.cs\" />\n",
        ["src/Sunder.Package.Agent.Provider.OpenAI/App/Sunder.Package.Agent.Provider.OpenAI.App.csproj"] =
            "    <Compile Include=\"..\\PackageDependency.Legacy.cs\" Link=\"PackageDependency.Legacy.cs\" />\n"
            + "    <Compile Include=\"..\\Management\\OpenAiOwnerManagementRpcClient.Legacy.cs\" Link=\"Management\\OpenAiOwnerManagementRpcClient.Legacy.cs\" />\n\0"
            + "    <Compile Include=\"..\\..\\Sunder.Package.Agent.Provider.Shared\\ProviderUtilityModelSelection.cs\" Link=\"Provider.Shared\\ProviderUtilityModelSelection.cs\" />\n",
        ["src/Sunder.Package.Agent.Tools.Files/Sunder.Package.Agent.Tools.Files.csproj"] =
            "    <SunderSdkCapability Include=\"logging.v1\" />\n"
            + "    <Compile Remove=\"Rpc\\**\\*.cs\" />\n",
        ["src/Sunder.Package.Agent.Tools.Shell/Sunder.Package.Agent.Tools.Shell.csproj"] =
            "    <SunderSdkCapability Include=\"logging.v1\" />\n"
            + "    <Compile Remove=\"Rpc\\**\\*.cs\" />\n",
        ["src/Sunder.Package.Agent.Tools.Web/Sunder.Package.Agent.Tools.Web.csproj"] =
            "    <SunderSdkCapability Include=\"logging.v1\" />\n"
            + "    <Compile Remove=\"Rpc\\**\\*.cs\" />\n",
        ["src/Sunder.Package.Agent.Builder/Sunder.Package.Agent.Builder.csproj"] =
            "    <Compile Include=\"PackageDependency.Legacy.cs\" />\n"
            + "    <Compile Include=\"Runtime\\BuilderRuntimeOperations.cs;Runtime\\BuilderWorkerComposition.cs;Runtime\\BuilderWorkerBindings.Legacy.cs\" />\n",
        ["src/Sunder.Package.Agent.Builder/Protocol/Sunder.Package.Agent.Builder.Protocol.csproj"] =
            "    <Compile Include=\"..\\Runtime\\BuilderRpcContracts.cs\" Link=\"BuilderRpcContracts.cs\" />\n"
            + "    <Compile Include=\"..\\App\\Runtime\\BuilderRpcExecutionTargetProxy.cs\" Link=\"BuilderRpcExecutionTargetProxy.cs\" />\n",
        ["src/Sunder.Package.Agent.Builder/App/Sunder.Package.Agent.Builder.App.csproj"] =
            "    <Compile Remove=\"Runtime\\BuilderRpcExecutionTargetProxy.cs\" />\n\0"
            + "    <Compile Include=\"..\\PackageDependency.Legacy.cs\" Link=\"PackageDependency.Legacy.cs\" />\n",
        ["src/Sunder.Package.Agent.Skills/Sunder.Package.Agent.Skills.csproj"] =
            "    <Compile Remove=\"Rpc\\ManagementService.cs;Rpc\\ManagementClient.cs\" />\n"
            + "    <Compile Remove=\"Services\\InstalledSkillRecord.cs;Runtime\\SkillRuntimeAbstractions.cs;Runtime\\SkillRuntimeContracts.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\SkillsProviderValues.cs;Rpc\\SelectableCapabilityProvider.cs;Rpc\\PromptContextProvider.cs;Rpc\\ToolSourceProvider.cs;Rpc\\SkillStackProvider.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\SkillsRuntimeBindings.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\ProviderComposition.cs;Rpc\\AppComposition.cs\" />\n",
        ["src/Sunder.Package.Agent.Skills/Protocol/SkillsProtocol.csproj"] =
            "    <Compile Include=\"..\\Services\\InstalledSkillRecord.cs\" Link=\"InstalledSkillRecord.cs\" />\n"
            + "    <Compile Include=\"..\\Runtime\\SkillRuntimeAbstractions.cs\" Link=\"SkillRuntimeAbstractions.cs\" />\n"
            + "    <Compile Include=\"..\\Runtime\\SkillRuntimeContracts.cs\" Link=\"SkillRuntimeContracts.cs\" />\n",
        ["src/Sunder.Package.Agent.Skills/App/SkillsApp.csproj"] =
            "    <Compile Include=\"..\\PackageDependency.Legacy.cs\" Link=\"PackageDependency.Legacy.cs\" />\n\0"
            + "    <Compile Include=\"..\\Runtime\\SkillOwnerRpc.Legacy.cs\" Link=\"Runtime\\SkillOwnerRpc.Legacy.cs\" />\n",
        ["src/Sunder.Package.Agent.Subagents/Sunder.Package.Agent.Subagents.csproj"] =
            "    <Compile Remove=\"Rpc\\ManagementService.cs;Rpc\\ManagementClient.cs\" />\n"
            + "    <Compile Remove=\"Services\\SubagentRecord.cs;Runtime\\SubagentRuntimeContracts.cs;Runtime\\TranscriptPageCursor.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\SubagentProviderValues.cs;Rpc\\SelectableCapabilityProvider.cs;Rpc\\ToolSourceProvider.cs;Rpc\\PromptContextProvider.cs;Rpc\\BehaviorLoopProvider.cs;Rpc\\SubagentStackProvider.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\RunControlClient.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\SubagentRuntimeBindings.cs;Rpc\\RuntimeCatalogClient.cs;Rpc\\ChildRunClient.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\SubagentRuntimeBindings.Catalogs.cs;Rpc\\CatalogClientInvocation.cs;Rpc\\ChatCatalogClient.cs;Rpc\\ToolCatalogClient.cs;Rpc\\BehaviorCatalogClient.cs;Rpc\\CapabilityCatalogClient.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\ProviderComposition.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\AppComposition.cs\" />\n",
        ["src/Sunder.Package.Agent.Subagents/Protocol/SubagentsProtocol.csproj"] =
            "    <Compile Include=\"..\\Services\\SubagentRecord.cs\" Link=\"SubagentRecord.cs\" />\n"
            + "    <Compile Include=\"..\\Runtime\\SubagentRuntimeContracts.cs\" Link=\"SubagentRuntimeContracts.cs\" />\n"
            + "    <Compile Include=\"..\\Runtime\\TranscriptPageCursor.cs\" Link=\"TranscriptPageCursor.cs\" />\n",
        ["src/Sunder.Package.Agent.Subagents/App/SubagentsApp.csproj"] =
            "    <Compile Include=\"..\\PackageDependency.Legacy.cs\" Link=\"PackageDependency.Legacy.cs\" />\n\0"
            + "    <Compile Include=\"..\\Runtime\\SubagentOwnerRpc.Legacy.cs\" Link=\"Runtime\\SubagentOwnerRpc.Legacy.cs\" />\n",
        ["src/Sunder.Package.Agent.Execution.Local/Sunder.Package.Agent.Execution.Local.csproj"] =
            "    <Compile Remove=\"Rpc\\ManagementService.cs;Rpc\\ManagementClient.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\AppComposition.cs;Rpc\\ProviderComposition.cs\" />\n"
            + "    <Compile Remove=\"Models\\LocalExecutionPresentationModels.cs;Runtime\\LocalExecutionPresentationDefaults.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\ExecutionValues.cs;Rpc\\ExecutionTargetProvider.cs;Rpc\\WorkspaceEditorProvider.cs;Rpc\\WorkspacePathMigratorProvider.cs\" />\n\0"
            + "    <!-- Preserve the reviewed Host compatibility floor after replacing manual composition. -->\n"
            + "    <SunderSdkCapability Include=\"background-services.v1\" />\n"
            + "    <SunderSdkCapability Include=\"runtime-generations.v1\" />\n",
        ["src/Sunder.Package.Agent.Execution.Local/Protocol/LocalProtocol.csproj"] =
            "    <Compile Include=\"..\\Models\\LocalExecutionPresentationModels.cs\" Link=\"LocalExecutionPresentationModels.cs\" />\n"
            + "    <Compile Include=\"..\\Runtime\\LocalExecutionPresentationDefaults.cs\" Link=\"LocalExecutionPresentationDefaults.cs\" />\n",
        ["src/Sunder.Package.Agent.Execution.Local/App/LocalApp.csproj"] =
            "    <Compile Include=\"..\\PackageDependency.Legacy.cs\" Link=\"PackageDependency.Legacy.cs\" />\n",
        ["src/Sunder.Package.Agent.Execution.Docker/Sunder.Package.Agent.Execution.Docker.csproj"] =
            "    <Compile Remove=\"Rpc\\ManagementService.cs;Rpc\\ManagementClient.cs;Rpc\\ImageCatalogContent.cs\" />\n"
            + "    <Compile Remove=\"Models\\DockerExecutionPresentationModels.cs;Runtime\\DockerExecutionPresentationDefaults.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\ExecutionValues.cs;Rpc\\ExecutionTargetProvider.cs;Rpc\\WorkspaceEditorProvider.cs;Rpc\\WorkspacePathMigratorProvider.cs;Rpc\\DockerStackProvider.cs;Rpc\\DockerWorkerBindings.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\ProviderComposition.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\AppComposition.cs\" />\n",
        ["src/Sunder.Package.Agent.Execution.Docker/Protocol/DockerProtocol.csproj"] =
            "    <Compile Include=\"..\\Models\\DockerExecutionPresentationModels.cs\" Link=\"DockerExecutionPresentationModels.cs\" />\n"
            + "    <Compile Include=\"..\\Runtime\\DockerExecutionPresentationDefaults.cs\" Link=\"DockerExecutionPresentationDefaults.cs\" />\n",
        ["src/Sunder.Package.Agent.Execution.Docker/App/DockerApp.csproj"] =
            "    <Compile Include=\"..\\PackageDependency.Legacy.cs\" Link=\"PackageDependency.Legacy.cs\" />\n",
        ["src/Sunder.Package.Agent.Memory.Semantic/Sunder.Package.Agent.Memory.Semantic.csproj"] =
            "    <Compile Remove=\"Rpc\\ManagementService.cs;Rpc\\ManagementClient.cs;Rpc\\ManagementModels.cs;Rpc\\MemoryValueMapper.cs;Rpc\\MemoryValueJson.cs;Rpc\\ProjectionContent.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\SessionCleanerProvider.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\ProviderComposition.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\AppComposition.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\ProfileCapabilityProvider.cs;Rpc\\DurableLifecycleProvider.cs;Rpc\\PromptContextProvider.cs;Rpc\\SemanticProviderValues.cs;Rpc\\MemoryProviderCatalog.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\GeneratedEmbeddingClient.cs;Rpc\\GeneratedRuntimeCatalogClient.cs;Rpc\\SemanticMemoryRpcCatalog.cs\" />\n"
            + "    <Compile Remove=\"Rpc\\MemoryAppRuntimeGateway.Transport.cs\" />\n"
            + "    <Compile Remove=\"Models\\MemoryInspectorModels.cs;Models\\SemanticEmbeddingContext.cs;Models\\SemanticMemoryInspectorModels.cs;Models\\SemanticMemoryServiceModels.cs;Runtime\\MemoryInspectorContracts.cs\" />\n",
        ["src/Sunder.Package.Agent.Memory.Semantic/Protocol/Sunder.Package.Agent.Memory.Semantic.Protocol.csproj"] =
            "    <Compile Include=\"..\\Models\\MemoryInspectorModels.cs\" Link=\"Models\\MemoryInspectorModels.cs\" />\n"
            + "    <Compile Include=\"..\\Models\\SemanticEmbeddingContext.cs\" Link=\"Models\\SemanticEmbeddingContext.cs\" />\n"
            + "    <Compile Include=\"..\\Models\\SemanticMemoryInspectorModels.cs\" Link=\"Models\\SemanticMemoryInspectorModels.cs\" />\n"
            + "    <Compile Include=\"..\\Models\\SemanticMemoryServiceModels.cs\" Link=\"Models\\SemanticMemoryServiceModels.cs\" />\n"
            + "    <Compile Include=\"..\\Runtime\\MemoryInspectorContracts.cs\" Link=\"MemoryInspectorContracts.cs\" />\n",
        ["src/Sunder.Package.Agent.Memory.Semantic/App/Sunder.Package.Agent.Memory.Semantic.App.csproj"] =
            "    <Compile Include=\"..\\PackageDependency.Legacy.cs\" Link=\"PackageDependency.Legacy.cs\" />\n",
        ["tests/Sunder.Package.Agent.Memory.Semantic.Tests/Sunder.Package.Agent.Memory.Semantic.Tests.csproj"] =
            "    <Compile Include=\"..\\Sunder.Package.Agent.Tests\\RegressionTestRpcState.FailureBoundary.cs\" Link=\"RegressionTestRpcState.FailureBoundary.cs\" />\n",
        ["tests/Sunder.Package.Agent.Presentation.Tests/Sunder.Package.Agent.Presentation.Tests.csproj"] =
            "    <Compile Include=\"..\\Sunder.Package.Agent.Tests\\RegressionTestRpcState.FailureBoundary.cs\"\n             Link=\"TestSupport\\RegressionTestRpcState.FailureBoundary.cs\" />\n",
    };

    internal static string BeforeAdditions(string root, string path, string normalizedText)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        normalizedText = ReadMsBuildTextBeforeCanonicalReplacement(root, path, normalizedText);
        return BeforeRecordedProjectAdditions(relative, normalizedText);
    }

    internal static string BeforeRecordedProjectAdditions(string relative, string normalizedText)
    {
        if (relative == "src/Sunder.Package.Agent.Memory.Semantic/Sunder.Package.Agent.Memory.Semantic.csproj")
        {
            const string current = "<PackageReference Include=\"Microsoft.Data.Sqlite\" SunderPackageAssetRole=\"worker\" />";
            Assert.Equal(2, normalizedText.Split(current, StringSplitOptions.None).Length);
            normalizedText = normalizedText.Replace(current, "<PackageReference Include=\"Microsoft.Data.Sqlite\" />", StringComparison.Ordinal);
        }
        if (!Additions.TryGetValue(relative, out var addition)) return normalizedText;
        if (relative == "src/Sunder.Package.Agent/Sunder.Package.Agent.csproj")
        {
            // The authentic index capture predates the final composition split and
            // worker-only SQLite tag. Do not manufacture the uncaptured 87-line image.
            const string later = "    <Compile Remove=\"Rpc\\Owner\\ProviderComposition.cs\" />\n"
                + "    <Compile Remove=\"Rpc\\Owner\\AppComposition.cs\" />\n";
            Assert.Equal(2, addition.Split(later, StringSplitOptions.None).Length);
            addition = addition.Replace(later, "    <Compile Remove=\"Rpc\\Owner\\AgentWorkerProviderCatalog.cs\" />\n", StringComparison.Ordinal);
            Assert.DoesNotContain("SunderPackageAssetRole", normalizedText, StringComparison.Ordinal);
        }
        foreach (var fragment in addition.Split('\0'))
        {
            Assert.Equal(2, normalizedText.Split(fragment, StringSplitOptions.None).Length);
            normalizedText = normalizedText.Replace(fragment, string.Empty, StringComparison.Ordinal);
        }
        return normalizedText;
    }

    internal static bool IsAddedLink(string project, string include)
        => (project is "src/Sunder.Package.Agent.Provider.Gemini/Sunder.Package.Agent.Provider.Gemini.csproj"
                or "src/Sunder.Package.Agent.Provider.Anthropic/Sunder.Package.Agent.Provider.Anthropic.csproj"
                or "src/Sunder.Package.Agent.Provider.LMStudio/Sunder.Package.Agent.Provider.LMStudio.csproj")
                && include == "../Sunder.Package.Agent.Provider.Shared/ProviderUtilityModelSelection.cs"
           || project == "src/Sunder.Package.Agent.Provider.OpenAI/App/Sunder.Package.Agent.Provider.OpenAI.App.csproj"
                && include == "../../Sunder.Package.Agent.Provider.Shared/ProviderUtilityModelSelection.cs"
           || project == "src/Sunder.Package.Agent.Contracts/Sunder.Package.Agent.Contracts.csproj"
               && (include is "../Sunder.Package.Agent.Provider.Shared/Models/ProviderChatModels.cs" or "../Sunder.Package.Agent.Shared/Execution/AgentExecutionFacetIds.cs"
                   || AgentSemanticHelpers.Any(path => include == "../" + path[4..]))
           || project == "src/Sunder.Package.Agent.Memory.Semantic/App/Sunder.Package.Agent.Memory.Semantic.App.csproj" && include == "../PackageDependency.Legacy.cs"
           || project == "src/Sunder.Package.Agent/App/Sunder.Package.Agent.App.csproj" && include == "../Runtime/Rpc/AgentToolBindings.Legacy.cs"
           || project == "src/Sunder.Package.Agent/OwnerProtocol/Sunder.Package.Agent.OwnerProtocol.csproj"
              && new[] { "../Services/AgentExecutionTargetWarmupResult.cs", "../Services/AgentToolCatalogEntry.cs", "../Services/AgentWorkspaceBindingRoles.cs",
                  "../Runtime/AgentTranscriptPageCursor.cs", "../Runtime/AgentWorkspaceDefaults.cs", "../App/Runtime/AgentRuntimeOperations.Legacy.cs", "../Runtime/AgentOwnerOperationException.cs",
                  "../Runtime/IAgentOwnerRuntimeProvider.cs" }.Contains(include, StringComparer.Ordinal)
           || project == "src/Sunder.Package.Agent.Memory.Semantic/Protocol/Sunder.Package.Agent.Memory.Semantic.Protocol.csproj"
              && new[] { "../Models/MemoryInspectorModels.cs", "../Models/SemanticEmbeddingContext.cs", "../Models/SemanticMemoryInspectorModels.cs",
                  "../Models/SemanticMemoryServiceModels.cs", "../Runtime/MemoryInspectorContracts.cs" }.Contains(include, StringComparer.Ordinal)
           || project == "src/Sunder.Package.Agent.Skills/Protocol/SkillsProtocol.csproj"
              && new[] { "../Services/InstalledSkillRecord.cs", "../Runtime/SkillRuntimeAbstractions.cs", "../Runtime/SkillRuntimeContracts.cs" }.Contains(include, StringComparer.Ordinal)
           || project == "src/Sunder.Package.Agent.Skills/App/SkillsApp.csproj"
              && new[] { "../PackageDependency.Legacy.cs", "../Runtime/SkillOwnerRpc.Legacy.cs" }.Contains(include, StringComparer.Ordinal)
           || project == "src/Sunder.Package.Agent.Subagents/Protocol/SubagentsProtocol.csproj"
              && new[] { "../Services/SubagentRecord.cs", "../Runtime/SubagentRuntimeContracts.cs", "../Runtime/TranscriptPageCursor.cs" }.Contains(include, StringComparer.Ordinal)
           || project == "src/Sunder.Package.Agent.Subagents/App/SubagentsApp.csproj"
              && new[] { "../PackageDependency.Legacy.cs", "../Runtime/SubagentOwnerRpc.Legacy.cs" }.Contains(include, StringComparer.Ordinal)
           || project == "src/Sunder.Package.Agent.Mcp/Shared/Sunder.Package.Agent.Mcp.Shared.csproj"
              && new[] { "../Runtime/McpManagementModels.cs", "../Runtime/McpManagementPayload.cs", "../Runtime/McpManagementContent.cs",
                  "../Services/McpManagementServiceModels.cs", "../Services/McpServerName.cs", "../Services/McpOAuthConstants.cs" }.Contains(include, StringComparer.Ordinal)
           || project == "src/Sunder.Package.Agent.Mcp/App/Sunder.Package.Agent.Mcp.App.csproj"
              && new[] { "../PackageDependency.Legacy.cs", "../Runtime/McpOwnerManagementRpcClient.Legacy.cs" }.Contains(include, StringComparer.Ordinal)
           || project == "src/Sunder.Package.Agent.Execution.Local/Protocol/LocalProtocol.csproj"
              && new[] { "../Models/LocalExecutionPresentationModels.cs", "../Runtime/LocalExecutionPresentationDefaults.cs" }.Contains(include, StringComparer.Ordinal)
           || project == "src/Sunder.Package.Agent.Execution.Local/App/LocalApp.csproj" && include == "../PackageDependency.Legacy.cs"
           || project == "src/Sunder.Package.Agent.Execution.Docker/Protocol/DockerProtocol.csproj"
              && new[] { "../Models/DockerExecutionPresentationModels.cs", "../Runtime/DockerExecutionPresentationDefaults.cs" }.Contains(include, StringComparer.Ordinal)
            || project == "src/Sunder.Package.Agent.Execution.Docker/App/DockerApp.csproj" && include == "../PackageDependency.Legacy.cs"
            || project == "src/Sunder.Package.Agent.Provider.Gemini/App/Sunder.Package.Agent.Provider.Gemini.App.csproj" && include == "../PackageDependency.Legacy.cs"
            || project == "src/Sunder.Package.Agent.Provider.Anthropic/App/Sunder.Package.Agent.Provider.Anthropic.App.csproj" && include == "../PackageDependency.Legacy.cs"
            || project == "src/Sunder.Package.Agent.Provider.LMStudio/App/Sunder.Package.Agent.Provider.LMStudio.App.csproj" && include == "../PackageDependency.Legacy.cs"
            || project == "src/Sunder.Package.Agent.Provider.OpenAI/Shared/Sunder.Package.Agent.Provider.OpenAI.Shared.csproj"
               && new[] { "../Management/OpenAiManagementModels.cs", "../Management/OpenAiUtilityModelSelection.cs" }.Contains(include, StringComparer.Ordinal)
            || project == "src/Sunder.Package.Agent.Provider.OpenAI/App/Sunder.Package.Agent.Provider.OpenAI.App.csproj"
               && new[] { "../PackageDependency.Legacy.cs", "../Management/OpenAiOwnerManagementRpcClient.Legacy.cs" }.Contains(include, StringComparer.Ordinal)
            || project == "src/Sunder.Package.Agent.Builder/Protocol/Sunder.Package.Agent.Builder.Protocol.csproj"
               && new[] { "../Runtime/BuilderRpcContracts.cs", "../App/Runtime/BuilderRpcExecutionTargetProxy.cs" }.Contains(include, StringComparer.Ordinal)
            || project == "src/Sunder.Package.Agent.Builder/App/Sunder.Package.Agent.Builder.App.csproj" && include == "../PackageDependency.Legacy.cs"
           || project.StartsWith("tests/", StringComparison.Ordinal)
           && Additions.ContainsKey(project)
           && include == FailureBoundaryInclude;

    internal static string BeforeMemoryResolverPort(string text)
    {
        var replacements = new (string After, string Before, int Count)[]
        {
            ("using Sunder.Package.Agent.Memory.Semantic.Runtime;", "using Sunder.Package.Agent.Protocol;", 1),
            ("public sealed partial class SemanticModelRuntimeResolver : IDisposable", "public sealed class SemanticModelRuntimeResolver : IDisposable", 1),
            ("private readonly ISemanticMemoryRpcCatalog _rpcCatalog;", "private readonly AgentRpcCatalog _rpcCatalog;", 1),
            ("        ISemanticMemoryRpcCatalog rpcCatalog,", "        AgentRpcCatalog rpcCatalog,", 1),
            ("_rpcCatalog.GetRuntimeCatalogs()", "_rpcCatalog.GetServiceReferences(AgentRpcServices.RuntimeCatalogs)", 2),
            ("        DisposeOwnedCatalogAdapter();\n", "", 1),
            ("    partial void DisposeOwnedCatalogAdapter();\n\n", "", 1),
            ("_rpcCatalog.GetEmbeddingProviders()", "_rpcCatalog.GetServiceReferences(AgentRpcServices.EmbeddingProviders)", 1),
            ("                        reference.Provider,", "                        reference.ToHandle(),", 1),
            ("        SunderRpcProviderSnapshot provider,", "        AgentRpcProviderHandle provider,", 1),
            ("SemanticProviderLease<IAgentRuntimeCatalog>", "AgentRpcLease<IAgentRuntimeCatalog>", 2),
            ("SemanticProviderReference<IAgentEmbeddingProvider>", "AgentRpcReference<IAgentEmbeddingProvider>", 2),
            ("    private void OnCatalogChanged()\n    {\n", "    private void OnCatalogChanged(object? sender, AgentRpcCatalogChangedEventArgs e)\n    {\n"
                + "        if (!e.IncludesContract(AgentRpcContractIds.EmbeddingProvider)\n"
                + "            && !e.IncludesContract(AgentRpcContractIds.RuntimeCatalog))\n        {\n            return;\n        }\n\n", 1),
        };
        foreach (var (after, before, count) in replacements)
        {
            Assert.Equal(count + 1, text.Split(after, StringSplitOptions.None).Length);
            text = text.Replace(after, before, StringComparison.Ordinal);
        }
        return text;
    }

    internal static string BeforeSkillsRuntimePort(string text)
    {
        const string start = "    private async ValueTask<IReadOnlyList<AgentResolvedExecutionResource>> ResolveResourcesAsync(";
        const string end = "    private InstalledSkillRecord? ResolveEnabledSkill(";
        Assert.Equal(2, text.Split(start, StringSplitOptions.None).Length);
        Assert.Equal(2, text.Split(end, StringSplitOptions.None).Length);
        var transport = File.ReadAllText(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "tests/Sunder.Package.Agent.Tests/Fixtures/SkillsFeature.transport-before-source.cs.txt")).Replace("\r\n", "\n", StringComparison.Ordinal);
        text = text[..text.IndexOf(start, StringComparison.Ordinal)] + transport.TrimEnd() + "\n\n" + text[text.IndexOf(end, StringComparison.Ordinal)..];
        var replacements = new (string After, string Before, int Count)[]
        {
            ("using Sunder.Package.Agent.Skills.Runtime;\n", "", 1),
            ("public sealed partial class SkillsFeature(SkillStore store, ISkillsRuntimeBindings runtime)", "public sealed class SkillsFeature(SkillStore store, AgentRpcCatalog rpcCatalog)", 1),
            ("        IAgentPromptContextContributor,\n        ISkillsExecutionFeature", "        IAgentPromptContextContributor", 1),
            ("    public ValueTask<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)\n"
                + "        => ExecuteAsync(context, request, context.ExecutionTargetReference?.ToHandle(), cancellationToken);\n\n"
                + "    public ValueTask<AgentPromptContextContribution?> ContributeContextAsync(AgentPromptContextRequest request, CancellationToken cancellationToken = default)\n"
                + "        => ContributeContextAsync(request, request.ExecutionTargetReference?.ToHandle(), cancellationToken);\n\n", "", 1),
            ("        AgentRpcProviderHandle? selectedTarget,\n", "", 4),
            ("await runtime.GetProfileAsync(context.ProfileId, cancellationToken).ConfigureAwait(false)", "ResolveProfile(context.ProfileId)", 1),
            ("await runtime.GetProfileAsync(request.Session.ProfileId, cancellationToken).ConfigureAwait(false)", "ResolveProfile(request.Session.ProfileId)", 1),
            ("profile, context, request, selectedTarget, cancellationToken", "profile, context, request, cancellationToken", 2),
            ("request.ExecutionBinding,\n            selectedTarget,", "request.ExecutionBinding,\n            request.ExecutionTargetReference,", 1),
            ("context.ExecutionBinding,\n            selectedTarget,", "context.ExecutionBinding,\n            context.ExecutionTargetReference,", 1),
            ("context.ExecutionBinding,\n                selectedTarget,", "context.ExecutionBinding,\n                context.ExecutionTargetReference,", 1),
        };
        foreach (var (after, before, count) in replacements)
        {
            Assert.Equal(count + 1, text.Split(after, StringSplitOptions.None).Length);
            text = text.Replace(after, before, StringComparison.Ordinal);
        }
        return text;
    }

    internal static string ReadPackageMetadataBeforeMigration(string path)
    {
        var metadata = File.ReadAllText(path);
        if (!new[] { "Sunder.Package.Agent.Memory.Semantic", "Sunder.Package.Agent.Skills", "Sunder.Package.Agent.Subagents", "Sunder.Package.Agent.Mcp", "Sunder.Package.Agent.Execution.Local", "Sunder.Package.Agent.Execution.Docker", "Sunder.Package.Agent.Provider.Gemini", "Sunder.Package.Agent.Provider.Anthropic", "Sunder.Package.Agent.Provider.LMStudio", "Sunder.Package.Agent.Provider.OpenAI", "Sunder.Package.Agent.Tools.Files", "Sunder.Package.Agent.Tools.Shell", "Sunder.Package.Agent.Tools.Web", "Sunder.Package.Agent.Builder" }.Any(name =>
                path.Replace('\\', '/').EndsWith("/src/" + name + "/PackageMetadata.cs", StringComparison.Ordinal))) return metadata;
        var dependency = ReadMigrationSourceText(Path.Combine(Path.GetDirectoryName(path)!, "PackageDependency.Legacy.cs"));
        Assert.DoesNotContain("SunderPackageDependency", metadata, StringComparison.Ordinal);
        const string prefix = "using Sunder.Sdk.Packaging;\n\n";
        dependency = dependency.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.StartsWith(prefix, dependency, StringComparison.Ordinal);
        Assert.StartsWith("[assembly: SunderPackageDependency(", dependency[prefix.Length..], StringComparison.Ordinal);
        return metadata.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n\n" + dependency[prefix.Length..];
    }

    internal static string BeforeSubagentRuntimeBindings(string path, string text)
    {
        text = BeforeSubagentAppComposition(path, text);
        if (!path.Replace('\\', '/').Contains("/src/Sunder.Package.Agent.Subagents/", StringComparison.Ordinal)) return text;
        text = BeforeCanonicalBehaviorFixes(path, text);
        var activation = Path.GetFileName(path) switch
        {
            "SubagentRuntimeChangeStream.cs" => "    internal void RefreshAfterActivation()\n    {\n        _capabilities.RefreshSubscriptions();\n        RefreshRuntime();\n    }\n\n",
            "SubagentEditorCapabilityCatalog.cs" => "    internal void RefreshSubscriptions() => RefreshProviderSubscriptions();\n\n",
            _ => null,
        };
        if (activation is not null)
        {
            Assert.Equal(2, text.Split(activation, StringSplitOptions.None).Length);
            text = text.Replace(activation, "", StringComparison.Ordinal);
        }
        text = BeforeSubagentCatalogBindings(path, text);
        var replacements = Path.GetFileName(path) switch
        {
            "SubagentFeature.cs" => new (string After, string Before, int Count)[]
            {
                ("using Sunder.Package.Agent.Subagents.Runtime;\n", "", 1),
                ("public sealed partial class SubagentFeature", "public sealed class SubagentFeature", 1),
                ("AgentRpcCatalog rpcCatalog,\n        ISubagentRuntimeBindings runtimeBindings)", "AgentRpcCatalog rpcCatalog)", 1),
                ("permissionStatusAdapter, runtimeBindings);", "permissionStatusAdapter);", 1),
                ("            runtimeBindings,", "            rpcCatalog,", 1),
            },
            "SubagentDescriptorSchema.cs" =>
            [
                ("using Sunder.Package.Agent.Subagents.Runtime;\n", "", 1),
                ("internal sealed partial class SubagentDescriptorSchema", "internal sealed class SubagentDescriptorSchema", 1),
                ("SubagentPermissionStatusAdapter permissionStatusAdapter,\n    ISubagentRuntimeBindings runtimeBindings)", "SubagentPermissionStatusAdapter permissionStatusAdapter)", 1),
                ("runtimeBindings.GetRuntimeCatalogs()", "rpcCatalog.GetServiceReferences(AgentRpcServices.RuntimeCatalogs)", 1),
            ],
            "SubagentChildRunCoordinator.cs" =>
            [
                ("using Sunder.Package.Agent.Subagents.Runtime;", "using Sunder.Package.Agent.Protocol;", 1),
                ("internal sealed partial class SubagentChildRunCoordinator", "internal sealed class SubagentChildRunCoordinator", 1),
                ("internal sealed partial record SubagentChildRunEnvironment", "internal sealed record SubagentChildRunEnvironment", 1),
                ("ISubagentRuntimeBindings runtimeBindings,", "AgentRpcCatalog rpcCatalog,", 1),
                ("runtimeBindings.GetRuntimeCatalogs()", "rpcCatalog.GetServiceReferences(AgentRpcServices.RuntimeCatalogs)", 1),
                ("var childRunExecutorReference = runtimeBindings\n            .GetChildRunExecutors()", "var childRunExecutorReference = rpcCatalog\n            .GetServiceReferences(AgentRpcServices.ChildRunExecutors)", 1),
                ("SubagentProviderReference<IAgentChildRunExecutor>", "AgentRpcReference<IAgentChildRunExecutor>", 2),
            ],
            "SubagentRuntimeBridge.cs" =>
            [
                ("internal sealed partial class SubagentRuntimeHandler", "internal sealed class SubagentRuntimeHandler", 1),
                ("AgentRpcCatalog rpcCatalog,\n    ISubagentRuntimeBindings runtimeBindings) : IDisposable", "AgentRpcCatalog rpcCatalog) : IDisposable", 1),
                ("runtimeBindings.GetRuntimeCatalogs()", "rpcCatalog.GetServiceReferences(AgentRpcServices.RuntimeCatalogs)", 1),
            ],
            "SubagentRuntimeChangeStream.cs" =>
            [
                ("internal sealed partial class SubagentRuntimeChangeStream", "internal sealed class SubagentRuntimeChangeStream", 1),
                ("private readonly ISubagentRuntimeBindings _runtimeBindings;", "private readonly AgentRpcCatalog _rpcCatalog;", 1),
                ("SubagentProviderReference<IAgentRuntimeCatalog>", "AgentRpcReference<IAgentRuntimeCatalog>", 1),
                ("SubagentProviderLease<IAgentRuntimeCatalog>", "AgentRpcLease<IAgentRuntimeCatalog>", 2),
                ("AgentRpcCatalog rpcCatalog,\n        ISubagentRuntimeBindings runtimeBindings)", "AgentRpcCatalog rpcCatalog)", 1),
                ("_runtimeBindings = runtimeBindings;", "_rpcCatalog = rpcCatalog;", 1),
                ("        _runtimeBindings.RuntimeCatalogChanged += OnRuntimeCatalogChanged;\n", "", 1),
                ("        _runtimeBindings.RuntimeCatalogChanged -= OnRuntimeCatalogChanged;\n", "", 1),
                ("\n    private void OnRuntimeCatalogChanged()\n    {\n        if (RefreshRuntime()) Publish(new(0, SubagentChangeKind.ResnapshotRequired));\n    }\n", "", 1),
                ("_runtimeBindings.GetRuntimeCatalogs()", "_rpcCatalog.GetServiceReferences(AgentRpcServices.RuntimeCatalogs)", 1),
            ],
            "SubsessionLocalRuntimeAdapter.cs" =>
            [
                ("using Sunder.Package.Agent.Contracts.Models;\n", "using Sunder.Package.Agent.Contracts.Models;\nusing Sunder.Package.Agent.Protocol;\n", 1),
                ("internal sealed partial class SubsessionLocalRuntimeAdapter", "internal sealed class SubsessionLocalRuntimeAdapter", 1),
                ("SubagentProviderReference<IAgentRuntimeCatalog>", "AgentRpcReference<IAgentRuntimeCatalog>", 2),
                ("SubagentProviderLease<IAgentRuntimeCatalog>", "AgentRpcLease<IAgentRuntimeCatalog>", 1),
            ],
            _ => [],
        };
        foreach (var (after, before, count) in replacements)
        {
            Assert.Equal(count + 1, text.Split(after, StringSplitOptions.None).Length);
            text = text.Replace(after, before, StringComparison.Ordinal);
        }
        return text;
    }

    internal static string ReconstructMemoryInspectorContracts(string path)
    {
        var ports = BeforeMemoryComposition(path, File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal));
        var modelsPath = Path.Combine(Path.GetDirectoryName(path)!, "../Models/MemoryInspectorModels.cs");
        var models = BeforeMemoryComposition(modelsPath, File.ReadAllText(modelsPath).Replace("\r\n", "\n", StringComparison.Ordinal));
        const string prefix = "using Sunder.Package.Agent.Contracts.Models;\nusing Sunder.Package.Agent.Memory.Semantic.Services;\n\nnamespace Sunder.Package.Agent.Memory.Semantic.Runtime;\n\n";
        Assert.StartsWith(prefix, models, StringComparison.Ordinal);
        Assert.DoesNotContain("public enum MemoryQueryKind", ports, StringComparison.Ordinal);
        return ports.TrimEnd() + "\n\n" + models[prefix.Length..];
    }

    internal static string ReconstructSkillsGateway(string path)
    {
        var text = BeforeSkillsComposition(path, File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal));
        var legacy = ReadMigrationSourceText(Path.Combine(Path.GetDirectoryName(path)!, "SkillOwnerRpc.Legacy.cs"));
        const string services = "using Sunder.Package.Agent.Skills.Services;";
        const string ownerJson = "internal static class OwnerJson";
        Assert.Equal(2, text.Split(services, StringSplitOptions.None).Length);
        Assert.Equal(2, text.Split(ownerJson, StringSplitOptions.None).Length);
        Assert.DoesNotContain("Skills.Protocol", text, StringComparison.Ordinal);
        text = text.Replace("private readonly Lazy<Task> _orphanedTransferCleanup = new(() => CleanupOrphanedTransferStagingAsync(context));",
                "private readonly Task _orphanedTransferCleanup = CleanupOrphanedTransferStagingAsync(context);", StringComparison.Ordinal)
            .Replace("    internal Task StartedTransferCleanup => _orphanedTransferCleanup.IsValueCreated ? _orphanedTransferCleanup.Value : Task.CompletedTask;\n", "", StringComparison.Ordinal)
            .Replace("internal Task OrphanedTransferCleanup => _orphanedTransferCleanup.Value;", "internal Task OrphanedTransferCleanup => _orphanedTransferCleanup;", StringComparison.Ordinal)
            .Replace("await OrphanedTransferCleanup.WaitAsync(cancellationToken)", "await _orphanedTransferCleanup.WaitAsync(cancellationToken)", StringComparison.Ordinal);
        var declarations = legacy[legacy.IndexOf("#if SUNDER_SKILLS_APP", StringComparison.Ordinal)..];
        return text.Replace(services, "using Sunder.Package.Agent.Skills.Protocol;\nusing Sunder.Package.Agent.Skills.Protocol.Generated;\n" + services, StringComparison.Ordinal)
            .Replace(ownerJson, declarations.TrimEnd() + "\n\n" + ownerJson, StringComparison.Ordinal);
    }

    internal static string ReconstructSubagentsOwnerRpc(string path)
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var legacy = ReadMigrationSourceText(Path.Combine(Path.GetDirectoryName(path)!, "SubagentOwnerRpc.Legacy.cs"));
        const string marker = "internal static class SubagentOwnerJson";
        Assert.DoesNotContain(marker, legacy, StringComparison.Ordinal);
        Assert.Equal(2, text.Split(marker, StringSplitOptions.None).Length);
        Assert.DoesNotContain("Subagents.Protocol", text, StringComparison.Ordinal);
        var replacements = new (string After, string Before)[]
        {
            ("internal sealed class SubagentOwnerProvider(ISubagentOwnerClient operations) : ISubagentsManagementProvider",
                "internal sealed class SubagentOwnerProvider(\n    SubagentRuntimeHandler handler,\n    SubagentRuntimeChangeStream changes) : ISubagentsManagementProvider"),
            ("    public SubagentOwnerProvider(SubagentRuntimeHandler handler, SubagentRuntimeChangeStream changes)\n"
                + "        : this(new SubagentRuntimeOwnerClient(handler, changes)) { }\n\n", ""),
            ("operations.QueryAsync", "handler.HandleAsync"),
            ("operations.CommandAsync", "handler.HandleAsync"),
            ("operations.SubscribeChangesAsync(", "changes.SubscribeAsync("),
        };
        foreach (var (after, before) in replacements)
        {
            Assert.Equal(2, legacy.Split(after, StringSplitOptions.None).Length);
            legacy = legacy.Replace(after, before, StringComparison.Ordinal);
        }
        return legacy.TrimEnd() + "\n\n" + text[text.IndexOf(marker, StringComparison.Ordinal)..];
    }
}
