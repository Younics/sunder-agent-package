using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentCompatibilityCandidateTestsRpcMigrationBaseline
{
    private const string BaselineRelativePath = "compatibility/agent-rpc-migration-baseline.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly MigrationBoundary[] Boundaries =
    [
        new("central", "src/Sunder.Package.Agent.Contracts"),
        new("agent-owner", "src/Sunder.Package.Agent/OwnerProtocol"),
        new("builder", "src/Sunder.Package.Agent.Builder/Protocol"),
        new("docker", "src/Sunder.Package.Agent.Execution.Docker/Protocol"),
        new("local", "src/Sunder.Package.Agent.Execution.Local/Protocol"),
        new("semantic-memory", "src/Sunder.Package.Agent.Memory.Semantic/Protocol"),
        new("anthropic", "src/Sunder.Package.Agent.Provider.Anthropic/Protocol"),
        new("gemini", "src/Sunder.Package.Agent.Provider.Gemini/Protocol"),
        new("lmstudio", "src/Sunder.Package.Agent.Provider.LMStudio/Protocol"),
        new("skills", "src/Sunder.Package.Agent.Skills/Protocol"),
        new("subagents", "src/Sunder.Package.Agent.Subagents/Protocol"),
        new("mcp", "src/Sunder.Package.Agent.Mcp/Shared"),
        new("openai", "src/Sunder.Package.Agent.Provider.OpenAI/Shared"),
    ];

    private static readonly IReadOnlyDictionary<string, SourceRelocationPolicy> ExplicitSourceRelocations =
        new Dictionary<string, SourceRelocationPolicy>(StringComparer.Ordinal)
        {
            ["src/Sunder.Package.Agent/OwnerProtocol/AgentOwnerRuntimeRpc.cs"] =
                MigrateSplit(
                    "src/Sunder.Package.Agent/Rpc/AgentOwnerRuntimeRpcProvider.cs; " +
                    "src/Sunder.Package.Agent/App/Rpc/AgentOwnerRuntimeRpcClient.cs; " +
                    "src/Sunder.Package.Agent/Runtime/AgentOwnerRuntimeRpcConversion.cs"),
            ["src/Sunder.Package.Agent/OwnerProtocol/AgentExecutionTargetWarmupResult.cs"] =
                Migrate("src/Sunder.Package.Agent/Services/AgentExecutionTargetWarmupResult.cs"),
            ["src/Sunder.Package.Agent/OwnerProtocol/AgentToolCatalogEntry.cs"] =
                Migrate("src/Sunder.Package.Agent/Services/AgentToolCatalogEntry.cs"),
            ["src/Sunder.Package.Agent/OwnerProtocol/AgentTranscriptPageCursor.cs"] =
                Migrate("src/Sunder.Package.Agent/Runtime/AgentTranscriptPageCursor.cs"),
            ["src/Sunder.Package.Agent/OwnerProtocol/AgentWorkspaceBindingRoles.cs"] =
                Migrate("src/Sunder.Package.Agent/Services/AgentWorkspaceBindingRoles.cs"),
            ["src/Sunder.Package.Agent/OwnerProtocol/AgentWorkspaceDefaults.cs"] =
                Migrate("src/Sunder.Package.Agent/Runtime/AgentWorkspaceDefaults.cs"),
            ["src/Sunder.Package.Agent.Builder/Protocol/BuilderRpcContracts.cs"] =
                MigrateSplit(
                    "src/Sunder.Package.Agent.Builder/Rpc/BuilderRpcContracts.cs; " +
                    "src/Sunder.Package.Agent.Builder/App/Rpc/BuilderRpcGateway.cs"),
            ["src/Sunder.Package.Agent.Builder/Protocol/BuilderRpcExecutionTargetProxy.cs"] =
                Migrate("src/Sunder.Package.Agent.Builder/App/Rpc/BuilderRpcExecutionTargetProxy.cs"),
            ["src/Sunder.Package.Agent.Builder/Protocol/BuilderRpcProtocol.cs"] = DeleteTransport(),
            ["src/Sunder.Package.Agent.Builder/Protocol/BuilderRpcWireMapper.cs"] = DeleteTransport(),
            ["src/Sunder.Package.Agent.Execution.Docker/Protocol/DockerExecutionPresentationRpc.cs"] =
                MigrateSplit(
                    "src/Sunder.Package.Agent.Execution.Docker/Models/DockerExecutionPresentationModels.cs; " +
                    "src/Sunder.Package.Agent.Execution.Docker/Runtime/DockerExecutionPresentationDefaults.cs; " +
                    "src/Sunder.Package.Agent.Execution.Docker/Runtime/DockerExecutionPresentationContentCodec.cs; " +
                    "src/Sunder.Package.Agent.Execution.Docker/Rpc/DockerExecutionPresentationProvider.cs; " +
                    "src/Sunder.Package.Agent.Execution.Docker/App/Rpc/DockerExecutionPresentationClient.cs"),
            ["src/Sunder.Package.Agent.Execution.Local/Protocol/LocalExecutionPresentationRpc.cs"] =
                MigrateSplit(
                    "src/Sunder.Package.Agent.Execution.Local/Models/LocalExecutionPresentationModels.cs; " +
                    "src/Sunder.Package.Agent.Execution.Local/Runtime/LocalExecutionPresentationDefaults.cs; " +
                    "src/Sunder.Package.Agent.Execution.Local/Rpc/LocalExecutionPresentationProvider.cs; " +
                    "src/Sunder.Package.Agent.Execution.Local/App/Rpc/LocalExecutionPresentationClient.cs"),
            ["src/Sunder.Package.Agent.Memory.Semantic/Protocol/MemoryInspectorContracts.cs"] =
                MigrateSplit(
                    "src/Sunder.Package.Agent.Memory.Semantic/Models/MemoryInspectorModels.cs; " +
                    "src/Sunder.Package.Agent.Memory.Semantic/Runtime/MemoryInspectorContracts.cs"),
            ["src/Sunder.Package.Agent.Memory.Semantic/Protocol/MemoryInspectorRpc.cs"] =
                MigrateSplit(
                    "src/Sunder.Package.Agent.Memory.Semantic/Runtime/MemoryProjectionContentCodec.cs; " +
                    "src/Sunder.Package.Agent.Memory.Semantic/Rpc/MemoryInspectorProvider.cs; " +
                    "src/Sunder.Package.Agent.Memory.Semantic/App/Rpc/MemoryInspectorClient.cs"),
            ["src/Sunder.Package.Agent.Memory.Semantic/Protocol/MemorySemanticProtocol.cs"] =
                DeleteTransport(),
            ["src/Sunder.Package.Agent.Memory.Semantic/Protocol/Models/SemanticEmbeddingContext.cs"] =
                Migrate("src/Sunder.Package.Agent.Memory.Semantic/Models/SemanticEmbeddingContext.cs"),
            ["src/Sunder.Package.Agent.Memory.Semantic/Protocol/Models/SemanticMemoryInspectorModels.cs"] =
                Migrate("src/Sunder.Package.Agent.Memory.Semantic/Models/SemanticMemoryInspectorModels.cs"),
            ["src/Sunder.Package.Agent.Memory.Semantic/Protocol/Models/SemanticMemoryServiceModels.cs"] =
                Migrate("src/Sunder.Package.Agent.Memory.Semantic/Models/SemanticMemoryServiceModels.cs"),
            ["src/Sunder.Package.Agent.Provider.Anthropic/Protocol/AnthropicCredentialProtocol.cs"] = DeleteTransport(),
            ["src/Sunder.Package.Agent.Provider.Gemini/Protocol/GeminiCredentialProtocol.cs"] = DeleteTransport(),
            ["src/Sunder.Package.Agent.Provider.LMStudio/Protocol/LMStudioCredentialProtocol.cs"] = DeleteTransport(),
            ["src/Sunder.Package.Agent.Skills/Protocol/InstalledSkillRecord.cs"] =
                Migrate("src/Sunder.Package.Agent.Skills/Services/InstalledSkillRecord.cs"),
            ["src/Sunder.Package.Agent.Skills/Protocol/SkillRuntimeAbstractions.cs"] =
                Migrate("src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeAbstractions.cs"),
            ["src/Sunder.Package.Agent.Skills/Protocol/SkillRuntimeContracts.cs"] =
                Migrate("src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeContracts.cs"),
            ["src/Sunder.Package.Agent.Skills/Protocol/SkillsOwnerProtocol.cs"] = DeleteTransport(),
            ["src/Sunder.Package.Agent.Subagents/Protocol/SubagentRecord.cs"] =
                Migrate("src/Sunder.Package.Agent.Subagents/Services/SubagentRecord.cs"),
            ["src/Sunder.Package.Agent.Subagents/Protocol/SubagentRuntimeContracts.cs"] =
                Migrate("src/Sunder.Package.Agent.Subagents/Runtime/SubagentRuntimeContracts.cs"),
            ["src/Sunder.Package.Agent.Subagents/Protocol/SubagentsOwnerProtocol.cs"] = DeleteTransport(),
            ["src/Sunder.Package.Agent.Subagents/Protocol/TranscriptPageCursor.cs"] =
                Migrate("src/Sunder.Package.Agent.Subagents/Runtime/TranscriptPageCursor.cs"),
            ["src/Sunder.Package.Agent.Mcp/Shared/McpManagementModels.cs"] =
                Migrate("src/Sunder.Package.Agent.Mcp/Runtime/McpManagementModels.cs"),
            ["src/Sunder.Package.Agent.Mcp/Shared/McpManagementPayload.cs"] =
                Migrate("src/Sunder.Package.Agent.Mcp/Runtime/McpManagementPayload.cs"),
            ["src/Sunder.Package.Agent.Mcp/Shared/McpManagementProtocol.cs"] =
                MigrateSplit(
                    "src/Sunder.Package.Agent.Mcp/Services/McpOAuthConstants.cs; " +
                    "src/Sunder.Package.Agent.Mcp/Rpc/McpManagementProvider.cs"),
            ["src/Sunder.Package.Agent.Mcp/Shared/McpManagementServiceModels.cs"] =
                Migrate("src/Sunder.Package.Agent.Mcp/Services/McpManagementServiceModels.cs"),
            ["src/Sunder.Package.Agent.Mcp/Shared/McpServerName.cs"] =
                Migrate("src/Sunder.Package.Agent.Mcp/Services/McpServerName.cs"),
            ["src/Sunder.Package.Agent.Provider.OpenAI/Shared/OpenAiManagementModels.cs"] =
                Migrate("src/Sunder.Package.Agent.Provider.OpenAI/Management/OpenAiManagementModels.cs"),
            ["src/Sunder.Package.Agent.Provider.OpenAI/Shared/OpenAiManagementProtocol.cs"] = DeleteTransport(),
            ["src/Sunder.Package.Agent.Provider.OpenAI/Shared/OpenAiUtilityModelSelection.cs"] =
                Migrate("src/Sunder.Package.Agent.Provider.OpenAI/Management/OpenAiUtilityModelSelection.cs"),
            ["src/Sunder.Package.Agent.Contracts/Execution/IAgentExecutionPathEnvironment.cs"] =
                ContractSource("sunder.agent.execution.target", "src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs"),
            ["src/Sunder.Package.Agent.Contracts/Execution/IAgentExecutionPathMapper.cs"] =
                ContractSource("sunder.agent.execution.target", "src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs"),
            ["src/Sunder.Package.Agent.Contracts/Execution/IAgentExecutionResourceResolver.cs"] =
                ContractSource("sunder.agent.execution.target", "src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs"),
            ["src/Sunder.Package.Agent.Contracts/Execution/IAgentExecutionScopeProvider.cs"] =
                ContractSource("sunder.agent.execution.target", "src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs"),
            ["src/Sunder.Package.Agent.Contracts/Execution/IAgentExecutionTarget.cs"] =
                ContractSource("sunder.agent.execution.target", "src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs"),
            ["src/Sunder.Package.Agent.Contracts/Execution/IAgentProcessExecutionTarget.cs"] =
                ContractSource("sunder.agent.execution.target", "src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs"),
            ["src/Sunder.Package.Agent.Contracts/Execution/IAgentRangedFileExecutionTarget.cs"] =
                ContractSource("sunder.agent.execution.target", "src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs"),
            ["src/Sunder.Package.Agent.Contracts/Execution/IAgentResourceAuthorityExecutionTarget.cs"] =
                ContractSource("sunder.agent.execution.target", "src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs"),
            ["src/Sunder.Package.Agent.Contracts/Execution/IAgentScopedInstructionDiscoveryTarget.cs"] =
                ContractSource("sunder.agent.execution.target", "src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs"),
            ["src/Sunder.Package.Agent.Contracts/Execution/IAgentStructuredFileSearchExecutionTarget.cs"] =
                ContractSource("sunder.agent.execution.target", "src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs"),
            ["src/Sunder.Package.Agent.Contracts/Execution/IAgentWorkspaceEditorContributor.cs"] =
                ContractSource("sunder.agent.workspace.editor", "src/Sunder.Package.Agent/Rpc/Public/WorkspaceEditor.cs"),
            ["src/Sunder.Package.Agent.Contracts/Execution/IAgentWorkspaceExecutionResolver.cs"] =
                ContractSource(
                    "sunder.agent.workspace.execution.resolver",
                    "src/Sunder.Package.Agent/Services/AgentWorkspaceExecutionResolver.cs"),
            ["src/Sunder.Package.Agent.Contracts/Execution/IAgentWorkspacePathMigrationContributor.cs"] =
                ContractSource("sunder.agent.workspace.path.migrator", "src/Sunder.Package.Agent/Rpc/Public/WorkspacePathMigrator.cs"),
            ["src/Sunder.Package.Agent.Contracts/Extensions/IAgentDurableLifecycleObserver.cs"] =
                ContractSource("sunder.agent.durable.lifecycle.observer", "src/Sunder.Package.Agent/Rpc/Public/DurableLifecycleObserver.cs"),
            ["src/Sunder.Package.Agent.Contracts/Extensions/IAgentProfileCapabilityConsumer.cs"] =
                ContractSource("sunder.agent.profile.capability.consumer", "src/Sunder.Package.Agent/Rpc/Public/ProfileCapabilityConsumer.cs"),
            ["src/Sunder.Package.Agent.Contracts/Extensions/IAgentProfileSelectableCapabilityChangeNotifier.cs"] =
                ContractSource("sunder.agent.selectable.capability.provider", "src/Sunder.Package.Agent/Rpc/Public/SelectableCapabilityProvider.cs"),
            ["src/Sunder.Package.Agent.Contracts/Extensions/IAgentProfileSelectableCapabilityProvider.cs"] =
                ContractSource("sunder.agent.selectable.capability.provider", "src/Sunder.Package.Agent/Rpc/Public/SelectableCapabilityProvider.cs"),
            ["src/Sunder.Package.Agent.Contracts/Extensions/IAgentPromptContextAcknowledgmentSink.cs"] =
                ContractSource(
                    "sunder.agent.prompt.context.contributor",
                    "src/Sunder.Package.Agent/Services/WorkspaceDocumentationContextService.cs"),
            ["src/Sunder.Package.Agent.Contracts/Extensions/IAgentPromptContextContributor.cs"] =
                ContractSource(
                    "sunder.agent.prompt.context.contributor",
                    "src/Sunder.Package.Agent/Services/WorkspaceDocumentationContextService.cs"),
            ["src/Sunder.Package.Agent.Contracts/Extensions/IAgentSystemPromptContributor.cs"] =
                ContractSource("sunder.agent.system.prompt.contributor", "src/Sunder.Package.Agent/Rpc/Public/SystemPromptContributor.cs"),
            ["src/Sunder.Package.Agent.Contracts/Providers/IAgentChatProvider.cs"] =
                ContractSource("sunder.agent.chat.provider", "src/Sunder.Package.Agent/Rpc/Public/ChatProvider.cs"),
            ["src/Sunder.Package.Agent.Contracts/Providers/IAgentEmbeddingProvider.cs"] =
                ContractSource("sunder.agent.embedding.provider", "src/Sunder.Package.Agent/Rpc/Public/EmbeddingProvider.cs"),
            ["src/Sunder.Package.Agent.Contracts/Providers/IAgentEmbeddingSpaceIdentityProvider.cs"] =
                ContractSource("sunder.agent.embedding.provider", "src/Sunder.Package.Agent/Rpc/Public/EmbeddingProvider.cs"),
            ["src/Sunder.Package.Agent.Contracts/Providers/IAgentUtilityModelProvider.cs"] =
                ContractSource("sunder.agent.chat.provider", "src/Sunder.Package.Agent/Rpc/Public/ChatProvider.cs"),
            ["src/Sunder.Package.Agent.Contracts/Runtime/AgentBehaviorLoopRuntimeCapabilities.cs"] =
                ContractSource("sunder.agent.run.control", "src/Sunder.Package.Agent/Runtime/Rpc/AgentRunControlProvider.cs"),
            ["src/Sunder.Package.Agent.Contracts/Runtime/IAgentBehaviorLoop.cs"] =
                ContractSource(
                    "sunder.agent.behavior.loop",
                    "src/Sunder.Package.Agent/Services/BehaviorLoops/DefaultAgentBehaviorLoop.cs"),
            ["src/Sunder.Package.Agent.Contracts/Runtime/IAgentBehaviorLoopRuntime.cs"] =
                ContractSource("sunder.agent.run.control", "src/Sunder.Package.Agent/Runtime/Rpc/AgentRunControlProvider.cs"),
            ["src/Sunder.Package.Agent.Contracts/Runtime/IAgentChildRunExecutor.cs"] =
                ContractSource("sunder.agent.child.run.executor", "src/Sunder.Package.Agent/Services/AgentRunCoordinator.cs"),
            ["src/Sunder.Package.Agent.Contracts/Runtime/IAgentInnerBehaviorLoopRuntime.cs"] =
                ContractSource("sunder.agent.run.control", "src/Sunder.Package.Agent/Runtime/Rpc/AgentRunControlProvider.cs"),
            ["src/Sunder.Package.Agent.Contracts/Runtime/IAgentRuntimeCatalog.cs"] =
                ContractSource("sunder.agent.runtime.catalog", "src/Sunder.Package.Agent/Services/AgentRuntimeCatalog.cs"),
            ["src/Sunder.Package.Agent.Contracts/Runtime/IAgentSessionDataCleaner.cs"] =
                ContractSource(
                    "sunder.agent.session.cleaner",
                    "src/Sunder.Package.Agent/Services/AgentAttachmentService.cs",
                    "src/Sunder.Package.Agent/HistorySearch/HistorySearchIndexingService.cs"),
            ["src/Sunder.Package.Agent.Contracts/Runtime/IAgentTranscriptCatalog.cs"] =
                ContractSource("sunder.agent.runtime.catalog", "src/Sunder.Package.Agent/Services/AgentRuntimeCatalog.cs"),
            ["src/Sunder.Package.Agent.Contracts/Tools/IAgentNativeToolSource.cs"] =
                ContractSource("sunder.agent.tool.source", "src/Sunder.Package.Agent/Rpc/Public/ToolSource.cs"),
            ["src/Sunder.Package.Agent.Contracts/Tools/IAgentPermissionAwareToolSource.cs"] =
                ContractSource("sunder.agent.tool.source", "src/Sunder.Package.Agent/Rpc/Public/ToolSource.cs"),
            ["src/Sunder.Package.Agent.Contracts/Tools/IAgentPermissionSurface.cs"] =
                ContractSource("sunder.agent.permission.surface", "src/Sunder.Package.Agent/Rpc/Public/PermissionSurface.cs"),
            ["src/Sunder.Package.Agent.Contracts/Tools/IAgentToolExecutionPreflightSource.cs"] =
                ContractSource("sunder.agent.tool.source", "src/Sunder.Package.Agent/Rpc/Public/ToolSource.cs"),
            ["src/Sunder.Package.Agent.Contracts/Tools/IAgentToolPresentationResolver.cs"] =
                ContractSource("sunder.agent.tool.source", "src/Sunder.Package.Agent/Rpc/Public/ToolSource.cs"),
            ["src/Sunder.Package.Agent.Contracts/Tools/IAgentToolSource.cs"] =
                ContractSource(
                    "sunder.agent.tool.execution.observed, sunder.agent.tool.source",
                    "src/Sunder.Package.Agent/Rpc/Public/ToolExecutionObserved.cs",
                    "src/Sunder.Package.Agent/Rpc/Public/ToolSource.cs"),
            ["src/Sunder.Package.Agent.Contracts/Rpc/AgentRpcCatalog.cs"] =
                Migrate("src/Sunder.Package.Agent/Runtime/Rpc/AgentRpcCatalog.cs"),
            ["src/Sunder.Package.Agent.Contracts/Rpc/AgentRpcCatalog.Async.cs"] =
                Migrate("src/Sunder.Package.Agent/Runtime/Rpc/AgentRpcCatalog.Async.cs"),
            ["src/Sunder.Package.Agent.Contracts/Rpc/AgentRpcContractDescriptors.cs"] = DeleteTransport(),
            ["src/Sunder.Package.Agent.Contracts/Rpc/AgentRpcWire.cs"] =
                SplitTransport(
                    "sunder.agent.behavior.loop, sunder.agent.child.run.executor, sunder.agent.run.control, " +
                    "sunder.agent.tool.execution.observed, sunder.agent.tool.source",
                    "src/Sunder.Package.Agent/Services/BehaviorLoops/DefaultAgentBehaviorLoop.cs",
                    "src/Sunder.Package.Agent/Services/AgentRunCoordinator.cs",
                    "src/Sunder.Package.Agent/Runtime/Rpc/AgentRunControlProvider.cs",
                    "src/Sunder.Package.Agent/Rpc/Public/ToolExecutionObserved.cs",
                    "src/Sunder.Package.Agent/Rpc/Public/ToolSource.cs"),
            ["src/Sunder.Package.Agent.Contracts/Rpc/AgentBehaviorLoopRpcBinding.cs"] =
                ReplaceBinding(
                    "sunder.agent.behavior.loop, sunder.agent.run.control",
                    "src/Sunder.Package.Agent/Runtime/Rpc/AgentRunControlRegistry.cs",
                    "src/Sunder.Package.Agent/Runtime/Rpc/AgentBehaviorLoopSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Subagents/Runtime/Rpc/AgentBehaviorLoopSemanticAdapter.cs"),
            ["src/Sunder.Package.Agent.Contracts/Rpc/AgentChatProviderRpcBinding.cs"] =
                ReplaceBinding(
                    "sunder.agent.chat.provider",
                    "src/Sunder.Package.Agent/Runtime/Rpc/ChatProviderSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Subagents/Runtime/Rpc/ChatProviderSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Provider.Anthropic/Rpc/ChatProviderSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Provider.Gemini/Rpc/ChatProviderSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Provider.LMStudio/Rpc/ChatProviderSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Provider.OpenAI/Rpc/ChatProviderSemanticAdapter.cs",
                    "src/Sunder.Package.Agent/Services/AgentFailurePresentationPolicy.cs"),
            ["src/Sunder.Package.Agent.Contracts/Rpc/AgentExecutionTargetRpcBinding.cs"] =
                ReplaceBinding(
                    "sunder.agent.execution.target",
                    "src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs",
                    "src/Sunder.Package.Agent/Runtime/Rpc/ExecutionTargetSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Builder/Rpc/ExecutionTargetSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Execution.Docker/Rpc/ExecutionTargetSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Execution.Local/Rpc/ExecutionTargetSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Skills/Runtime/Rpc/ExecutionTargetSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Tools.Files/Rpc/ExecutionTargetSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Tools.Shell/Rpc/ExecutionTargetSemanticAdapter.cs"),
            ["src/Sunder.Package.Agent.Contracts/Rpc/AgentObservedToolExecutionRpcBinding.cs"] =
                ReplaceBinding(
                    "sunder.agent.tool.execution.observed",
                    "src/Sunder.Package.Agent/Rpc/Public/ToolExecutionObserved.cs",
                    "src/Sunder.Package.Agent/Runtime/Rpc/ObservedToolExecutionSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Mcp/Rpc/ObservedToolExecutionSemanticAdapter.cs"),
            ["src/Sunder.Package.Agent.Contracts/Rpc/AgentRpcSimpleBindings.cs"] =
                ReplaceBinding(
                    "sunder.agent.behavior.loop, sunder.agent.chat.provider, sunder.agent.child.run.executor, " +
                    "sunder.agent.durable.lifecycle.observer, sunder.agent.embedding.provider, " +
                    "sunder.agent.execution.target, " +
                    "sunder.agent.permission.surface, sunder.agent.profile.capability.consumer, " +
                    "sunder.agent.prompt.context.contributor, sunder.agent.run.control, " +
                    "sunder.agent.selectable.capability.provider, sunder.agent.session.cleaner, " +
                    "sunder.agent.system.prompt.contributor, sunder.agent.tool.execution.observed, " +
                    "sunder.agent.tool.source, sunder.agent.runtime.catalog, sunder.agent.workspace.editor, " +
                    "sunder.agent.workspace.execution.resolver, sunder.agent.workspace.path.migrator",
                    "src/Sunder.Package.Agent/Runtime/Rpc/PromptContextSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Memory.Semantic/Rpc/PromptContextSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Skills/Rpc/PromptContextSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Subagents/Rpc/PromptContextSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Tools.Files/Rpc/PromptContextSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Tools.Shell/Rpc/PromptContextSemanticAdapter.cs",
                    "src/Sunder.Package.Agent/Runtime/Rpc/SelectableCapabilitySemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Mcp/Rpc/SelectableCapabilitySemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Skills/Rpc/SelectableCapabilitySemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Subagents/Rpc/SelectableCapabilitySemanticAdapter.cs"),
            ["src/Sunder.Package.Agent.Contracts/Rpc/AgentRuntimeCatalogRpcBinding.cs"] =
                ReplaceBinding(
                    "sunder.agent.runtime.catalog",
                    "src/Sunder.Package.Agent/Runtime/Rpc/RuntimeCatalogSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Mcp/Runtime/Rpc/RuntimeCatalogSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Memory.Semantic/Runtime/Rpc/RuntimeCatalogSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Skills/Runtime/Rpc/RuntimeCatalogSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Subagents/Runtime/Rpc/RuntimeCatalogSemanticAdapter.cs"),
            ["src/Sunder.Package.Agent.Contracts/Rpc/AgentToolSourceRpcBinding.cs"] =
                ReplaceBinding(
                    "sunder.agent.tool.source",
                    "src/Sunder.Package.Agent/Rpc/Public/ToolSource.cs",
                    "src/Sunder.Package.Agent/Runtime/Rpc/ToolSourceSemanticAdapter.cs",
                    "src/Sunder.Package.Agent/App/Rpc/ToolSourceSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Mcp/Rpc/ToolSourceSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Skills/Rpc/ToolSourceSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Subagents/Rpc/ToolSourceSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Tools.Files/Rpc/ToolSourceSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Tools.Shell/Rpc/ToolSourceSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Tools.Web/Rpc/ToolSourceSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Tools.Web/Services/AgentStaticToolSourceAdapter.cs"),
            ["src/Sunder.Package.Agent.Contracts/Rpc/AgentWorkspaceResolverRpcBinding.cs"] =
                ReplaceBinding(
                    "sunder.agent.workspace.execution.resolver",
                    "src/Sunder.Package.Agent/Runtime/Rpc/WorkspaceExecutionResolverSemanticAdapter.cs",
                    "src/Sunder.Package.Agent.Builder/Rpc/WorkspaceExecutionResolverSemanticAdapter.cs"),
        };

    private static readonly IReadOnlyDictionary<string, ContractMigrationPolicy> ContractMigrationPolicies =
        CreateContractMigrationPolicies();

    private static IReadOnlyDictionary<string, ContractMigrationPolicy> CreateContractMigrationPolicies()
        => new Dictionary<string, ContractMigrationPolicy>(StringComparer.Ordinal)
        {
            ["sunder.agent.behavior.loop"] = PublicPolicy(
                "owner-implemented",
                "src/Sunder.Package.Agent/Services/BehaviorLoops/DefaultAgentBehaviorLoop.cs",
                "open",
                Providers(
                    ("agent.behavior.loop", "sunder.package.agent"),
                    ("subagents.behavior.loop", "sunder.package.agent.subagents"))),
            ["sunder.agent.chat.provider"] = PublicPolicy(
                "declaration-only",
                "src/Sunder.Package.Agent/Rpc/Public/ChatProvider.cs",
                "open",
                Providers(
                    ("anthropic.chat", "sunder.package.agent.provider.anthropic"),
                    ("gemini.chat", "sunder.package.agent.provider.gemini"),
                    ("lmstudio.chat", "sunder.package.agent.provider.lmstudio"),
                    ("openai.chat", "sunder.package.agent.provider.openai"))),
            ["sunder.agent.child.run.executor"] = PublicPolicy(
                "owner-implemented",
                "src/Sunder.Package.Agent/Services/AgentRunCoordinator.cs",
                "authority-package-only",
                Providers(("agent.child.runs", "sunder.package.agent"))),
            ["sunder.agent.durable.lifecycle.observer"] = PublicPolicy(
                "declaration-only",
                "src/Sunder.Package.Agent/Rpc/Public/DurableLifecycleObserver.cs",
                "open",
                Providers(("semantic.memory.lifecycle", "sunder.package.agent.memory.semantic"))),
            ["sunder.agent.embedding.provider"] = PublicPolicy(
                "declaration-only",
                "src/Sunder.Package.Agent/Rpc/Public/EmbeddingProvider.cs",
                "open",
                Providers(
                    ("gemini.embedding", "sunder.package.agent.provider.gemini"),
                    ("lmstudio.embedding", "sunder.package.agent.provider.lmstudio"),
                    ("openai.embedding", "sunder.package.agent.provider.openai"))),
            ["sunder.agent.execution.target"] = PublicPolicy(
                "declaration-only",
                "src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs",
                "open",
                Providers(
                    ("docker.execution.target", "sunder.package.agent.execution.docker"),
                    ("local.execution.target", "sunder.package.agent.execution.local"))),
            ["sunder.agent.permission.surface"] = PublicPolicy(
                "declaration-only",
                "src/Sunder.Package.Agent/Rpc/Public/PermissionSurface.cs",
                "open",
                Providers(
                    ("files.permissions", "sunder.package.agent.tools.files"),
                    ("shell.permissions", "sunder.package.agent.tools.shell"))),
            ["sunder.agent.profile.capability.consumer"] = PublicPolicy(
                "declaration-only",
                "src/Sunder.Package.Agent/Rpc/Public/ProfileCapabilityConsumer.cs",
                "open",
                Providers(("semantic.memory.profile.capabilities", "sunder.package.agent.memory.semantic"))),
            ["sunder.agent.prompt.context.contributor"] = PublicPolicy(
                "owner-implemented",
                "src/Sunder.Package.Agent/Services/WorkspaceDocumentationContextService.cs",
                "open",
                Providers(
                    ("agent.workspace.documentation", "sunder.package.agent"),
                    ("files.prompt.context", "sunder.package.agent.tools.files"),
                    ("semantic.memory.prompt.context", "sunder.package.agent.memory.semantic"),
                    ("shell.prompt.context", "sunder.package.agent.tools.shell"),
                    ("skills.prompt.context", "sunder.package.agent.skills"),
                    ("subagents.prompt.context", "sunder.package.agent.subagents"))),
            ["sunder.agent.run.control"] = PublicPolicy(
                "owner-implemented",
                "src/Sunder.Package.Agent/Runtime/Rpc/AgentRunControlProvider.cs",
                "authority-package-only",
                Providers(("agent.run.control", "sunder.package.agent"))),
            ["sunder.agent.runtime.catalog"] = PublicPolicy(
                "owner-implemented",
                "src/Sunder.Package.Agent/Services/AgentRuntimeCatalog.cs",
                "authority-package-only",
                Providers(("agent.runtime.catalog", "sunder.package.agent"))),
            ["sunder.agent.selectable.capability.provider"] = PublicPolicy(
                "declaration-only",
                "src/Sunder.Package.Agent/Rpc/Public/SelectableCapabilityProvider.cs",
                "open",
                Providers(
                    ("mcp.selectable.capabilities", "sunder.package.agent.mcp"),
                    ("skills.selectable.capabilities", "sunder.package.agent.skills"),
                    ("subagents.selectable.capabilities", "sunder.package.agent.subagents"))),
            ["sunder.agent.session.cleaner"] = PublicPolicy(
                "owner-implemented",
                "src/Sunder.Package.Agent/Services/AgentAttachmentService.cs; " +
                "src/Sunder.Package.Agent/HistorySearch/HistorySearchIndexingService.cs",
                "open",
                Providers(
                    ("agent.attachment.cleaner", "sunder.package.agent"),
                    ("agent.history.cleaner", "sunder.package.agent"),
                    ("files.session.cleaner", "sunder.package.agent.tools.files"),
                    ("mcp.session.cleaner", "sunder.package.agent.mcp"),
                    ("openai.continuation.cleaner", "sunder.package.agent.provider.openai"),
                    ("semantic.memory.session.cleaner", "sunder.package.agent.memory.semantic"))),
            ["sunder.agent.system.prompt.contributor"] = PublicPolicy(
                "declaration-only",
                "src/Sunder.Package.Agent/Rpc/Public/SystemPromptContributor.cs",
                "open",
                []),
            ["sunder.agent.tool.execution.observed"] = PublicPolicy(
                "declaration-only",
                "src/Sunder.Package.Agent/Rpc/Public/ToolExecutionObserved.cs",
                "allowlist",
                Providers(("mcp.tools.observed", "sunder.package.agent.mcp")),
                ["sunder.package.agent.mcp"]),
            ["sunder.agent.tool.source"] = PublicPolicy(
                "declaration-only",
                "src/Sunder.Package.Agent/Rpc/Public/ToolSource.cs",
                "open",
                Providers(
                    ("files.tools", "sunder.package.agent.tools.files"),
                    ("mcp.tools", "sunder.package.agent.mcp"),
                    ("shell.tools", "sunder.package.agent.tools.shell"),
                    ("skills.tools", "sunder.package.agent.skills"),
                    ("subagents.tools", "sunder.package.agent.subagents"),
                    ("web.tools", "sunder.package.agent.tools.web"))),
            ["sunder.agent.workspace.editor"] = PublicPolicy(
                "declaration-only",
                "src/Sunder.Package.Agent/Rpc/Public/WorkspaceEditor.cs",
                "open",
                Providers(
                    ("docker.workspace.editor", "sunder.package.agent.execution.docker"),
                    ("local.workspace.editor", "sunder.package.agent.execution.local"))),
            ["sunder.agent.workspace.execution.resolver"] = PublicPolicy(
                "owner-implemented",
                "src/Sunder.Package.Agent/Services/AgentWorkspaceExecutionResolver.cs",
                "authority-package-only",
                Providers(("agent.workspace.execution.resolver", "sunder.package.agent"))),
            ["sunder.agent.workspace.path.migrator"] = PublicPolicy(
                "declaration-only",
                "src/Sunder.Package.Agent/Rpc/Public/WorkspacePathMigrator.cs",
                "open",
                Providers(
                    ("docker.workspace.path.migrator", "sunder.package.agent.execution.docker"),
                    ("local.workspace.path.migrator", "sunder.package.agent.execution.local"))),
        };

    private static ContractMigrationPolicy PublicPolicy(
        string sourceClassification,
        string finalSourceLocation,
        string providerPublicationPolicy,
        IReadOnlyList<ProviderOwnership> providers,
        IReadOnlyList<string>? providerPackageAllowlist = null)
        => new(
            sourceClassification,
            finalSourceLocation,
            "package",
            "sunder.package.agent",
            providerPublicationPolicy,
            providerPackageAllowlist ?? [],
            providers);

    private static IReadOnlyList<ProviderOwnership> Providers(
        params (string ProviderId, string PackageId)[] providers)
        => providers
            .Select(static provider => new ProviderOwnership(provider.ProviderId, provider.PackageId))
            .OrderBy(static provider => provider.ProviderId, StringComparer.Ordinal)
            .ToArray();

    private static SourceRelocationPolicy Migrate(string destination)
        => new("migrate-business-source", destination);

    private static SourceRelocationPolicy MigrateSplit(string destination)
        => new("migrate-split-business-source", destination);

    private static SourceRelocationPolicy DeleteTransport()
        => new("delete-generator-transport-workaround", "generated role-local implementation-first binding");

    private static SourceRelocationPolicy ContractSource(
        string contractIds,
        params string[] sourceDestinations)
        => new(
            "consolidate-contract-source-delete-shared-facade",
            ValidateSemanticDestinations(sourceDestinations),
            ParseContractIds(contractIds));

    private static SourceRelocationPolicy SplitTransport(
        string contractIds,
        params string[] semanticDestinations)
        => new(
            "migrate-semantic-policy-delete-manual-transport",
            ValidateSemanticDestinations(semanticDestinations),
            ParseContractIds(contractIds));

    private static SourceRelocationPolicy ReplaceBinding(
        string contractIds,
        params string[] semanticDestinations)
        => new(
            "migrate-semantic-adapters-delete-manual-transport",
            ValidateSemanticDestinations(semanticDestinations),
            ParseContractIds(contractIds));

    private static IReadOnlyList<string> ParseContractIds(string contractIds)
    {
        var parsed = contractIds
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (parsed.Length == 0 || parsed.Distinct(StringComparer.Ordinal).Count() != parsed.Length)
        {
            throw new InvalidDataException("Semantic RPC relocation contract IDs must be explicit and unique.");
        }

        return parsed;
    }

    private static string ValidateSemanticDestinations(IReadOnlyList<string> destinations)
    {
        if (destinations.Count == 0
            || destinations.Any(static destination =>
                !destination.StartsWith("src/", StringComparison.Ordinal)
                || destination.Contains('*', StringComparison.Ordinal)
                || destination.Contains("owning package", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Semantic RPC relocation destinations must be explicit source paths.");
        }

        return string.Join("; ", destinations);
    }

    [Fact]
    public void RpcMigrationBaseline_MatchesDescriptorAndSourceInventory()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        var baselinePath = Path.Combine(root, NormalizePath(BaselineRelativePath));
        AgentSourceMigrationProjectChanges.AssertRetirementCurrentTopology(root);
        var generated = Generate(root);
        // This is the frozen before-image, not the rolling release candidate.
        // Approved project replacements need historical-source reconciliation;
        // the candidate-update switch must never rewrite this evidence.
        Assert.True(File.Exists(baselinePath), $"RPC migration baseline was not found: {baselinePath}");
        Assert.Equal(Encoding.UTF8.GetString(File.ReadAllBytes(baselinePath)), Encoding.UTF8.GetString(generated));
    }

    private static byte[] Generate(string root)
    {
        var declarations = ReadDeclarations(root);
        var descriptors = ReadDescriptors(root, declarations);
        var descriptorKeys = descriptors
            .Select(static descriptor => (descriptor.ContractId, descriptor.Version))
            .ToHashSet();
        var externalProviders = declarations.Providers
            .Where(provider => !descriptorKeys.Contains((
                provider.Metadata.GetValueOrDefault("ContractId") ?? string.Empty,
                provider.Metadata.GetValueOrDefault("ContractVersion") ?? string.Empty)))
            .ToArray();
        var externalUses = declarations.Uses
            .Where(use => !descriptorKeys.Contains((
                use.Include,
                use.Metadata.GetValueOrDefault("ContractVersion") ?? string.Empty)))
            .ToArray();
        var externalBundles = declarations.Bundles
            .Where(bundle => !descriptorKeys.Contains((
                bundle.Metadata.GetValueOrDefault("ContractId") ?? string.Empty,
                bundle.Metadata.GetValueOrDefault("Version") ?? string.Empty)))
            .ToArray();
        var sources = ReadMigrationSources(root);
        var centralSources = sources.Where(static source => source.Boundary == "central").ToArray();
        var helperSources = sources.Where(static source => source.Boundary != "central").ToArray();
        var buildTopology = ReadBuildTopology(root, declarations);
        // New source-migration candidates are proven separately against this
        // immutable oracle; all 96 original adapter sources remain hash-checked.
        var candidates = BuilderSourceMigrationTests.SourcePaths.Concat(AgentPublicSourceMigrationTests.SourcePaths).Concat(AgentOwnerSourceMigrationTests.SourcePaths)
            .Concat(AgentCanonicalSourceMigrationTests.SourcePaths)
            .Append(AgentAppSourceMigrationTests.CompositionSource)
            .Concat(AgentSourceMigrationProjectChanges.ReadHistoricalCandidateSources("AgentCanonicalSourceMigrationTests.EmbeddingSources")).Append(AgentCanonicalSourceMigrationTests.LegacyEmbeddingBindings)
            .Concat(AgentCanonicalSourceMigrationTests.ChatSources).Append(AgentCanonicalSourceMigrationTests.LegacyChatBindings)
            .Concat(AgentCanonicalSourceMigrationTests.BehaviorSources).Append(AgentCanonicalSourceMigrationTests.LegacyBehaviorBindings)
            .Append(AgentSourceMigrationProjectChanges.ProviderChatModelsSource)
            .Append(AgentSourceMigrationProjectChanges.LegacyProviderCatalogSource)
            .Concat(AgentSourceMigrationProjectChanges.AgentSemanticHelpers)
            .Concat([AgentSourceMigrationProjectChanges.ExecutionFacetSource, "src/Sunder.Agent.Execution.Common/FileSearchRequestValidation.cs", "src/Sunder.Agent.Execution.Common/ProcessExecutionLimits.cs"])
            .Concat(AgentAppSourceMigrationTests.WorkspaceEditorSources).Append(AgentAppSourceMigrationTests.LegacyWorkspaceEditorBindings)
            .Concat(AgentCanonicalSourceMigrationTests.CleanupSources).Append(AgentCanonicalSourceMigrationTests.LegacyCleanupBindings)
            .Concat(AgentCanonicalSourceMigrationTests.MetadataSources).Append(AgentCanonicalSourceMigrationTests.LegacyMetadataBindings)
            .Concat(AgentCanonicalSourceMigrationTests.SelectableSources).Append(AgentCanonicalSourceMigrationTests.LegacySelectableBindings)
            .Concat(AgentCanonicalSourceMigrationTests.WorkspacePathSources).Append(AgentCanonicalSourceMigrationTests.LegacyWorkspacePathBindings)
            .Concat(AgentSourceMigrationProjectChanges.ReadHistoricalCandidateSources("AgentCanonicalSourceMigrationTests.ToolProfileSources")).Append(AgentCanonicalSourceMigrationTests.LegacyToolProfileBindings)
            .Concat(AgentCanonicalSourceMigrationTests.PromptContextSources).Append(AgentCanonicalSourceMigrationTests.LegacyPromptContextBindings)
            .Concat(AgentCanonicalSourceMigrationTests.ExecutionSources).Append(AgentCanonicalSourceMigrationTests.LegacyExecutionBindings)
            .Concat(AgentCanonicalSourceMigrationTests.ToolSources).Append(AgentCanonicalSourceMigrationTests.LegacyToolBindings)
            .Concat(AgentCanonicalSourceMigrationTests.ObservedToolSources).Append(AgentCanonicalSourceMigrationTests.LegacyObservedToolBindings)
            .Concat(CredentialSourceMigrationTests.SourcePaths).Concat(AgentSourceMigrationProjectChanges.ReadHistoricalCandidateSources("McpManagementSourceMigrationTests.SourcePaths"))
            .Concat(AgentSourceMigrationProjectChanges.ReadHistoricalCandidateSources("SkillsManagementSourceMigrationTests.SourcePaths"))
            .Concat(SubagentsManagementSourceMigrationTests.SourcePaths)
            .Concat(AgentSourceMigrationProjectChanges.ReadHistoricalCandidateSources("LocalPresentationSourceMigrationTests.SourcePaths"))
            .Concat(AgentSourceMigrationProjectChanges.ReadHistoricalCandidateSources("DockerPresentationSourceMigrationTests.SourcePaths"))
            .Concat(MemoryInspectorSourceMigrationTests.SourcePaths)
            .Append(AgentSourceArtifactMigrationTests.MemoryCleanerSource)
            .Concat(AgentSourceArtifactMigrationTests.MemoryProviderSources)
            .Concat(AgentSourceArtifactMigrationTests.MemoryConsumerSources)
            .Concat(AgentSourceArtifactMigrationTests.MemoryWorkerSources)
            .Concat(AgentSourceArtifactMigrationTests.MemoryAppTransportSources)
            .Append(AgentSourceArtifactMigrationTests.MemoryAppCompositionSource)
            .Concat(AgentSourceArtifactMigrationTests.MemoryBusinessModelSources)
            .Concat(AgentSourceArtifactMigrationTests.SkillsRelocationSources)
            .Concat(AgentSourceArtifactMigrationTests.SkillsConsumerSources)
            .Concat(AgentSourceArtifactMigrationTests.SubagentsRelocationSources)
            .Concat(AgentSourceArtifactMigrationTests.SubagentsConsumerSources)
            .Concat(AgentSourceArtifactMigrationTests.SubagentsCatalogSources)
            .Concat(AgentSourceArtifactMigrationTests.McpRelocationSources)
            .Concat(AgentSourceArtifactMigrationTests.McpConsumerSources)
            .Concat(AgentSourceArtifactMigrationTests.LocalRelocationSources)
            .Concat(AgentSourceArtifactMigrationTests.DockerRelocationSources)
            .Concat(AgentSourceArtifactMigrationTests.GeminiRelocationSources)
            .Concat(AgentSourceArtifactMigrationTests.AnthropicRelocationSources)
            .Concat(AgentSourceArtifactMigrationTests.LMStudioRelocationSources)
            .Concat(AgentSourceArtifactMigrationTests.OpenAiRelocationSources)
            .Concat(AgentSourceArtifactMigrationTests.FilesRelocationSources)
            .Concat(AgentSourceArtifactMigrationTests.ShellRelocationSources)
            .Concat(AgentSourceArtifactMigrationTests.WebRelocationSources)
            .Concat(AgentSourceArtifactMigrationTests.BuilderRelocationSources)
            .Concat(AgentAppSourceMigrationTests.RelocatedModels)
            .Append(AgentAppSourceMigrationTests.LegacyOperations)
            .Append(AgentAppSourceMigrationTests.OperationErrorSource)
            .Concat(new[]
            {
                "src/Sunder.Package.Agent.Provider.OpenAI/Rpc/ManagementService.cs",
                "src/Sunder.Package.Agent.Provider.OpenAI/Rpc/ManagementClient.cs",
                "src/Sunder.Package.Agent.Provider.OpenAI/Management/OpenAiRuntimeManagementClient.cs",
            }).Where(path => !AgentSourceMigrationProjectChanges.HasOrdinarySourceHistory(path)
                && path != "src/Sunder.Package.Agent/Runtime/AgentRuntimeContracts.cs").ToArray();
        Assert.Equal(370, candidates.Length);
        // These ordinary business/mapping sources contain no RPC surface markers.
        var domainMappings = new[]
        {
            "src/Sunder.Package.Agent.Builder/Rpc/AppComposition.cs",
            "src/Sunder.Package.Agent.Mcp/Rpc/AppComposition.cs",
            "src/Sunder.Package.Agent.Provider.OpenAI/Rpc/AppComposition.cs",
            "src/Sunder.Package.Agent.Provider.OpenAI/Rpc/ProviderComposition.cs",
            "src/Sunder.Package.Agent.Execution.Local/Rpc/AppComposition.cs",
            "src/Sunder.Package.Agent.Execution.Local/Rpc/ProviderComposition.cs",
            "src/Sunder.Package.Agent.Execution.Docker/Rpc/AppComposition.cs",
            "src/Sunder.Package.Agent.Mcp/Worker/McpActivation.cs",
            "src/Sunder.Package.Agent.Mcp/Rpc/ProviderComposition.cs",
            "src/Sunder.Package.Agent.Execution.Docker/Rpc/ProviderComposition.cs",
            "src/Sunder.Package.Agent.Memory.Semantic/MemoryLocalStore.Composition.cs",
            "src/Sunder.Package.Agent.Memory.Semantic/Rpc/ProviderComposition.cs",
            "src/Sunder.Package.Agent.Subagents/Runtime/SubagentsActivation.cs",
            "src/Sunder.Package.Agent.Subagents/Rpc/ProviderComposition.cs",
            "src/Sunder.Package.Agent.Subagents/Rpc/AppComposition.cs",
            "src/Sunder.Package.Agent.Skills/Runtime/SkillsActivation.cs",
            "src/Sunder.Package.Agent.Skills/Rpc/ProviderComposition.cs",
            "src/Sunder.Package.Agent.Skills/Rpc/AppComposition.cs",
            "src/Sunder.Package.Agent.Provider.Gemini/Runtime/ProviderSettings.cs",
            "src/Sunder.Package.Agent.Provider.Gemini/Rpc/CredentialSettings.Composition.cs",
            "src/Sunder.Package.Agent.Provider.Anthropic/Runtime/ProviderSettings.cs",
            "src/Sunder.Package.Agent.Provider.Anthropic/Rpc/CredentialSettings.Composition.cs",
            "src/Sunder.Package.Agent.Provider.LMStudio/Runtime/ProviderSettings.cs",
            "src/Sunder.Package.Agent.Provider.LMStudio/Rpc/CredentialSettings.Composition.cs",
            "src/Sunder.Package.Agent/Rpc/Consumers/AgentRpcContractIds.cs",
            AgentSourceMigrationProjectChanges.ExecutionFacetSource,
            "src/Sunder.Agent.Execution.Common/FileSearchRequestValidation.cs",
            "src/Sunder.Agent.Execution.Common/ProcessExecutionLimits.cs",
            "src/Sunder.Package.Agent/Rpc/Public/RunControlMapping.Context.cs",
            "src/Sunder.Package.Agent/Rpc/Public/RunControlMapping.Tools.cs",
            "src/Sunder.Package.Agent.Mcp/Runtime/McpRuntimeManagementClient.cs",
            "src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeOwnerClient.cs",
            "src/Sunder.Package.Agent.Subagents/Runtime/SubagentRuntimeOwnerClient.cs",
            "src/Sunder.Package.Agent.Memory.Semantic/Rpc/MemoryValueMapper.cs",
            "src/Sunder.Package.Agent.Memory.Semantic/PackageDependency.Legacy.cs",
            "src/Sunder.Package.Agent.Memory.Semantic/Models/MemoryInspectorModels.cs",
            "src/Sunder.Package.Agent.Memory.Semantic/Models/SemanticEmbeddingContext.cs",
            "src/Sunder.Package.Agent.Memory.Semantic/Models/SemanticMemoryInspectorModels.cs",
            "src/Sunder.Package.Agent.Memory.Semantic/Models/SemanticMemoryServiceModels.cs",
            "src/Sunder.Package.Agent.Skills/Services/InstalledSkillRecord.cs",
            "src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeAbstractions.cs",
            "src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeContracts.cs",
            "src/Sunder.Package.Agent.Skills/PackageDependency.Legacy.cs",
            "src/Sunder.Package.Agent.Skills/Runtime/ISkillsRuntimeBindings.cs",
            "src/Sunder.Package.Agent.Subagents/Services/SubagentRecord.cs",
            "src/Sunder.Package.Agent.Subagents/Runtime/SubagentRuntimeContracts.cs",
            "src/Sunder.Package.Agent.Subagents/Runtime/TranscriptPageCursor.cs",
            "src/Sunder.Package.Agent.Subagents/PackageDependency.Legacy.cs",
            "src/Sunder.Package.Agent.Subagents/Runtime/SubsessionLocalRuntimeAdapter.Legacy.cs",
            "src/Sunder.Package.Agent.Subagents/Runtime/SubagentCatalogContracts.cs",
            "src/Sunder.Package.Agent.Mcp/Runtime/McpManagementModels.cs",
            "src/Sunder.Package.Agent.Mcp/Services/McpManagementServiceModels.cs",
            "src/Sunder.Package.Agent.Mcp/Services/McpServerName.cs",
            "src/Sunder.Package.Agent.Mcp/Services/McpOAuthConstants.cs",
            "src/Sunder.Package.Agent.Mcp/PackageDependency.Legacy.cs",
            "src/Sunder.Package.Agent.Mcp/Runtime/IMcpWorkspaceCatalog.cs",
            "src/Sunder.Package.Agent.Execution.Local/Models/LocalExecutionPresentationModels.cs",
            "src/Sunder.Package.Agent.Execution.Local/Runtime/LocalExecutionPresentationDefaults.cs",
            "src/Sunder.Package.Agent.Execution.Local/PackageDependency.Legacy.cs",
            "src/Sunder.Package.Agent.Execution.Local/Runtime/LocalWorkerConfiguration.cs",
            "src/Sunder.Package.Agent.Execution.Docker/Models/DockerExecutionPresentationModels.cs",
            "src/Sunder.Package.Agent.Execution.Docker/Runtime/DockerExecutionPresentationDefaults.cs",
            "src/Sunder.Package.Agent.Execution.Docker/Runtime/DockerWorkerConfiguration.cs",
            "src/Sunder.Package.Agent.Execution.Docker/PackageDependency.Legacy.cs",
            "src/Sunder.Package.Agent.Provider.Gemini/PackageDependency.Legacy.cs",
            "src/Sunder.Package.Agent.Provider.Gemini/Runtime/GeminiWorkerComposition.cs",
            "src/Sunder.Package.Agent.Provider.Anthropic/PackageDependency.Legacy.cs",
            "src/Sunder.Package.Agent.Provider.Anthropic/Runtime/AnthropicWorkerComposition.cs",
            "src/Sunder.Package.Agent.Provider.LMStudio/PackageDependency.Legacy.cs",
            "src/Sunder.Package.Agent.Provider.LMStudio/Runtime/LMStudioWorkerComposition.cs",
            "src/Sunder.Package.Agent.Provider.OpenAI/Management/OpenAiManagementModels.cs",
            "src/Sunder.Package.Agent.Provider.OpenAI/Management/OpenAiUtilityModelSelection.cs",
            "src/Sunder.Package.Agent.Provider.OpenAI/Management/OpenAiAuthOperationHandler.cs",
            "src/Sunder.Package.Agent.Provider.OpenAI/PackageDependency.Legacy.cs",
            "src/Sunder.Package.Agent.Tools.Files/PackageDependency.Legacy.cs",
            "src/Sunder.Package.Agent.Tools.Files/Runtime/FileExecutionReference.cs",
            "src/Sunder.Package.Agent.Tools.Files/FilesToolSource.Legacy.cs",
            "src/Sunder.Package.Agent.Tools.Files/Runtime/FilesExecutionFacets.Legacy.cs",
            "src/Sunder.Package.Agent.Tools.Files/Rpc/FilesExecutionFacets.cs",
            "src/Sunder.Package.Agent.Tools.Files/Rpc/ExecutionValues.cs",
            "src/Sunder.Package.Agent.Tools.Files/Rpc/ExecutionClient.cs",
            "src/Sunder.Package.Agent.Tools.Shell/Runtime/ShellExecutionReference.cs",
            "src/Sunder.Package.Agent.Tools.Shell/ShellToolSource.Legacy.cs",
            "src/Sunder.Package.Agent.Tools.Shell/Rpc/ExecutionClient.cs",
            "src/Sunder.Package.Agent.Tools.Shell/PackageDependency.Legacy.cs",
            "src/Sunder.Package.Agent.Tools.Web/PackageDependency.Legacy.cs",
            "src/Sunder.Package.Agent.Tools.Web/Services/WebToolSource.cs",
            "src/Sunder.Package.Agent.Tools.Web/Runtime/WebWorkerComposition.cs",
            "src/Sunder.Package.Agent.Tools.Web/Runtime/WebWorkerBindings.Legacy.cs",
            "src/Sunder.Package.Agent.Builder/Runtime/BuilderRuntimeOperations.cs",
            "src/Sunder.Package.Agent.Builder/App/Runtime/BuilderRpcExecutionTargetProxy.cs",
            "src/Sunder.Package.Agent.Builder/PackageDependency.Legacy.cs",
            "src/Sunder.Package.Agent/Services/AgentExecutionTargetWarmupResult.cs",
            "src/Sunder.Package.Agent/Services/AgentToolCatalogEntry.cs",
            "src/Sunder.Package.Agent/Services/AgentWorkspaceBindingRoles.cs",
            "src/Sunder.Package.Agent/Runtime/AgentTranscriptPageCursor.cs",
            "src/Sunder.Package.Agent/Runtime/AgentWorkspaceDefaults.cs",
            "src/Sunder.Package.Agent/Runtime/IAgentOwnerResources.cs",
            "src/Sunder.Package.Agent/Runtime/AgentOwnerOperationException.cs",
            "src/Sunder.Package.Agent/Runtime/Rpc/AgentEmbeddingBindings.Legacy.cs",
            "src/Sunder.Package.Agent/Rpc/Consumers/AgentEmbeddingBindings.cs",
            "src/Sunder.Package.Agent/Runtime/Rpc/AgentChatBindings.Legacy.cs",
            "src/Sunder.Package.Agent/Rpc/Consumers/AgentChatBindings.cs",
            "src/Sunder.Package.Agent/App/Runtime/AgentWorkspaceEditorBindings.Legacy.cs",
            "src/Sunder.Package.Agent/Rpc/Consumers/AgentWorkspaceEditorBindings.cs",
            "src/Sunder.Package.Agent/Runtime/Rpc/AgentCleanupBindings.Legacy.cs",
            "src/Sunder.Package.Agent/Rpc/Consumers/AgentCleanupBindings.cs",
            "src/Sunder.Package.Agent/Runtime/Rpc/AgentMetadataBindings.Legacy.cs",
            "src/Sunder.Package.Agent/Rpc/Consumers/AgentMetadataBindings.cs",
            "src/Sunder.Package.Agent/Runtime/Rpc/AgentSelectableBindings.Legacy.cs",
            "src/Sunder.Package.Agent/Rpc/Consumers/AgentSelectableBindings.cs",
            "src/Sunder.Package.Agent/Runtime/Rpc/AgentWorkspacePathBindings.Legacy.cs",
            "src/Sunder.Package.Agent/Rpc/Consumers/AgentWorkspacePathBindings.cs",
            "src/Sunder.Package.Agent/Runtime/IAgentToolProfileCatalog.cs",
            "src/Sunder.Package.Agent/Rpc/Consumers/AgentToolProfileBindings.cs",
            "src/Sunder.Package.Agent/Runtime/Rpc/AgentPromptContextBindings.Legacy.cs",
            "src/Sunder.Package.Agent/Rpc/Consumers/AgentPromptContextBindings.cs",
            "src/Sunder.Package.Agent/Runtime/Rpc/AgentExecutionBindings.Legacy.cs",
            "src/Sunder.Package.Agent/Rpc/Consumers/AgentExecutionBindings.cs",
            "src/Sunder.Package.Agent/Rpc/Consumers/AgentExecutionValues.cs",
            "src/Sunder.Package.Agent/Rpc/Consumers/GeneratedExecutionTargetClient.Facets.cs",
            "src/Sunder.Package.Agent/Runtime/Rpc/AgentToolBindings.Legacy.cs",
            "src/Sunder.Package.Agent/Runtime/Rpc/AgentObservedToolBindings.Legacy.cs",
            "src/Sunder.Package.Agent/Rpc/Consumers/AgentToolBindings.cs",
            "src/Sunder.Package.Agent/Rpc/Consumers/AgentObservedToolBindings.cs",
        };
        var expectedCandidates = candidates.Except(domainMappings, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var actualCandidates = buildTopology.RpcAdapterSources.Where(source => candidates.Contains(source.Path, StringComparer.Ordinal))
            .Select(source => source.Path).Order(StringComparer.Ordinal).ToArray();
        Assert.True(expectedCandidates.SequenceEqual(actualCandidates, StringComparer.Ordinal),
            "Historical candidate closure changed. Missing: " + string.Join(", ", expectedCandidates.Except(actualCandidates))
            + "; extra: " + string.Join(", ", actualCandidates.Except(expectedCandidates)));
        buildTopology = buildTopology with
        {
            RpcAdapterSources = buildTopology.RpcAdapterSources.Where(source =>
                !candidates.Contains(source.Path, StringComparer.Ordinal)).ToArray(),
        };

        Assert.Equal(31, descriptors.Count);
        Assert.Equal(19, descriptors.Count(static descriptor => descriptor.Exposure == "public"));
        Assert.Equal(12, descriptors.Count(static descriptor => descriptor.Exposure == "package"));
        Assert.Equal(19, ContractMigrationPolicies.Count);
        Assert.Equal(7, descriptors.Count(static descriptor => descriptor.MigrationPolicy?.SourceClassification == "owner-implemented"));
        Assert.Equal(12, descriptors.Count(static descriptor => descriptor.MigrationPolicy?.SourceClassification == "declaration-only"));
        Assert.Equal(14, descriptors.Count(static descriptor => descriptor.MigrationPolicy?.ProviderPublicationPolicy == "open"));
        Assert.Equal(4, descriptors.Count(static descriptor => descriptor.MigrationPolicy?.ProviderPublicationPolicy == "authority-package-only"));
        Assert.Single(descriptors, static descriptor => descriptor.MigrationPolicy?.ProviderPublicationPolicy == "allowlist");
        Assert.Equal(11, descriptors.Count(static descriptor => descriptor.Exposure == "public" && descriptor.Providers.Count > 1));
        Assert.Equal(45, descriptors.Where(static descriptor => descriptor.Exposure == "public").Sum(static descriptor => descriptor.Providers.Count));
        Assert.Equal(45, ContractMigrationPolicies.Values.Sum(static policy => policy.ExpectedProviders.Count));
        Assert.All(
            descriptors.Where(static descriptor => descriptor.Exposure == "public"),
            static descriptor =>
            {
                Assert.Equal("package", descriptor.MigrationPolicy!.AuthorityKind);
                Assert.Equal("sunder.package.agent", descriptor.MigrationPolicy.AuthorityId);
                Assert.StartsWith("src/", descriptor.MigrationPolicy.FinalSourceLocation, StringComparison.Ordinal);
            });
        Assert.Empty(descriptors.Single(static descriptor => descriptor.ContractId == "sunder.agent.system.prompt.contributor").Providers);
        Assert.Equal(31, descriptors.Sum(static descriptor => descriptor.Bindings.Count));
        Assert.Equal(57, descriptors.Sum(static descriptor => descriptor.Providers.Count));
        Assert.Equal(46, descriptors.Sum(static descriptor => descriptor.Uses.Count));
        Assert.All(declarations.Providers, static declaration => Assert.False(string.IsNullOrWhiteSpace(declaration.OwnerPackageId)));
        Assert.All(declarations.Uses, static declaration => Assert.False(string.IsNullOrWhiteSpace(declaration.OwnerPackageId)));
        Assert.Equal(6, externalProviders.Length);
        Assert.All(externalProviders, static provider =>
            Assert.Equal("sunder.stack.contributor", provider.Metadata.GetValueOrDefault("ContractId")));
        Assert.Empty(externalUses);
        Assert.Single(externalBundles);
        Assert.Equal("sunder.stack.contributor", externalBundles[0].Metadata.GetValueOrDefault("ContractId"));
        Assert.Equal(63, descriptors.Sum(static descriptor => descriptor.Providers.Count) + externalProviders.Length);
        Assert.Equal(46, descriptors.Sum(static descriptor => descriptor.Uses.Count));
        Assert.Equal(63, descriptors.Sum(static descriptor => descriptor.Bundles.Count) + externalBundles.Length);
        Assert.Equal(216, centralSources.Length);
        Assert.Equal(83, helperSources.Length);
        Assert.Equal(28, helperSources.Count(static source => source.Disposition.StartsWith("migrate", StringComparison.Ordinal)));
        Assert.Equal(55, helperSources.Count(static source => !source.Disposition.StartsWith("migrate", StringComparison.Ordinal)));
        Assert.Equal(31, sources.Count(static source => source.Disposition == "compatibility-corpus-then-delete"));
        Assert.DoesNotContain(sources, static source => source.Destination.Contains("owning package", StringComparison.Ordinal));
        Assert.Equal(38, centralSources.Count(static source => source.Disposition == "consolidate-contract-source-delete-shared-facade"));
        Assert.Equal(9, centralSources.Count(static source => source.Disposition.StartsWith("migrate-semantic-", StringComparison.Ordinal)));
        Assert.All(
            centralSources.Where(static source => source.ContractIds is not null),
            source =>
            {
                Assert.NotEmpty(source.ContractIds!);
                Assert.All(source.ContractIds!, contractId => Assert.Contains(contractId, ContractMigrationPolicies.Keys));
            });
        Assert.Equal(57, buildTopology.Projects.Count);
        Assert.Equal(13, buildTopology.Projects.Count(static project => project.Disposition == "delete-rpc-helper-project-after-source-relocation"));
        var helperProjects = buildTopology.Projects
            .Where(static project => project.Disposition == "delete-rpc-helper-project-after-source-relocation")
            .ToArray();
        Assert.Equal(11, helperProjects.Count(static project => project.Role == "protocol"));
        Assert.Equal(2, helperProjects.Count(static project => project.Role == "rpc-helper"));
        Assert.Equal(189, buildTopology.ProjectReferences.Count);
        Assert.Equal(252, buildTopology.LinkedSourceDeclarations.Count);
        Assert.Equal(31, buildTopology.GeneratedOutputs.Count);
        using (var inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, BaselineRelativePath))))
        {
            var expectedPaths = inventory.RootElement.GetProperty("buildTopology").GetProperty("rpcAdapterSources")
                .EnumerateArray().Select(value => value.GetProperty("path").GetString()!).ToArray();
            var actualPaths = buildTopology.RpcAdapterSources.Select(value => value.Path).ToArray();
            Assert.True(buildTopology.RpcAdapterSources.Count == 96 && expectedPaths.ToHashSet(StringComparer.Ordinal).SetEquals(actualPaths),
                "Historical source closure changed. Missing: " + string.Join(", ", expectedPaths.Except(actualPaths))
                + "; extra: " + string.Join(", ", actualPaths.Except(expectedPaths)));
        }
        Assert.DoesNotContain(
            buildTopology.RpcAdapterSources,
            static source => source.Disposition.Contains("-or-", StringComparison.Ordinal));

        var baseline = new RpcMigrationBaseline(
            SchemaVersion: 3,
            Purpose: "Pre-implementation-first Agent RPC migration oracle; not a build input or release authorization.",
            SourceHashPolicy: "SHA-256 of UTF-8 text after CRLF/CR normalization to LF.",
            Descriptors: descriptors,
            ExternalContractProviders: externalProviders,
            ExternalContractUses: externalUses,
            ExternalContractBundles: externalBundles,
            BuildTopology: buildTopology,
            SourceRelocations: sources);
        return JsonSerializer.SerializeToUtf8Bytes(baseline, JsonOptions);
    }

    private static DeclarationInventory ReadDeclarations(string root)
    {
        var sources = AgentSourceMigrationProjectChanges.EnumerateMigrationHistoryFiles(root, Path.Combine(root, "src"), "*")
            .Concat(Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Where(static path => Path.GetExtension(path) is ".csproj" or ".props" or ".targets")
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new DeclarationInventory(
            ReadMsBuildDeclarations(root, sources, "SunderRpcBinding"),
            ReadMsBuildDeclarations(root, sources, "SunderRpcProvider"),
            ReadMsBuildDeclarations(root, sources, "SunderUsesContract"),
            ReadMsBuildDeclarations(root, sources, "SunderContractBundle"));
    }

    private static IReadOnlyList<MsBuildDeclaration> ReadMsBuildDeclarations(
        string root,
        IReadOnlyList<string> sources,
        string itemName)
        => sources
            .SelectMany(source =>
            {
                var document = AgentSourceMigrationProjectChanges.ReadMsBuildHistoryDocument(root, source);
                var projectPackageId = ReadProjectProperty(document, "SunderPackageFamilyId");
                var projectRole = ReadProjectProperty(document, "SunderPackageRole");
                return document.Descendants(itemName)
                    .Select(item =>
                    {
                        var condition = ReadCondition(item);
                        var relativeSource = RelativePath(root, source);
                        return new MsBuildDeclaration(
                            relativeSource,
                            projectPackageId
                            ?? ReadConditionProperty(condition, "SunderPackageFamilyId")
                            ?? InferOwnerPackageId(relativeSource),
                            projectRole
                            ?? ReadConditionProperty(condition, "SunderPackageRole")
                            ?? (IsHelperBoundaryPath(relativeSource) ? "rpc-helper" : null),
                            condition,
                            NormalizeRelativePath(item.Attribute("Include")?.Value ?? string.Empty),
                            ReadMetadata(item));
                    });
            })
            .OrderBy(static declaration => declaration.Project, StringComparer.Ordinal)
            .ThenBy(static declaration => declaration.OwnerPackageId, StringComparer.Ordinal)
            .ThenBy(static declaration => declaration.Role, StringComparer.Ordinal)
            .ThenBy(static declaration => declaration.Include, StringComparer.Ordinal)
            .ThenBy(static declaration => declaration.Condition, StringComparer.Ordinal)
            .ThenBy(static declaration => string.Join('\0', declaration.Metadata.Select(static pair => $"{pair.Key}={pair.Value}")), StringComparer.Ordinal)
            .ToArray();

    private static string? ReadProjectProperty(XDocument document, string name)
        => document.Descendants(name)
            .Where(static property => property.Attribute("Condition") is null)
            .Select(static property => property.Value.Trim())
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static string? ReadCondition(XElement item)
    {
        var conditions = item.AncestorsAndSelf()
            .Reverse()
            .Select(static element => element.Attribute("Condition")?.Value.Trim())
            .Where(static condition => !string.IsNullOrWhiteSpace(condition))
            .Select(static condition => $"({condition})")
            .ToArray();
        return conditions.Length == 0 ? null : string.Join(" and ", conditions);
    }

    private static string? ReadConditionProperty(string? condition, string propertyName)
    {
        if (condition is null)
        {
            return null;
        }

        var match = Regex.Match(
            condition,
            $@"'\$\({Regex.Escape(propertyName)}\)'\s*==\s*'([^']+)'",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static IReadOnlyDictionary<string, string> ReadMetadata(XElement item)
    {
        var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in item.Attributes().Where(static attribute =>
                     attribute.Name.LocalName is not "Include" and not "Condition"))
        {
            metadata[attribute.Name.LocalName] = NormalizeRelativePath(attribute.Value.Trim());
        }
        foreach (var child in item.Elements())
        {
            metadata[child.Name.LocalName] = NormalizeRelativePath(child.Value.Trim());
        }
        return metadata;
    }

    private static IReadOnlyList<DescriptorSnapshot> ReadDescriptors(
        string root,
        DeclarationInventory declarations)
    {
        var publicRoot = NormalizeRelativePath("src/Sunder.Package.Agent.Contracts/Contracts") + "/";
        return AgentSourceMigrationProjectChanges.EnumerateMigrationHistoryFiles(root, Path.Combine(root, "src"), "*.rpc.json")
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Select(path =>
            {
                var relativePath = RelativePath(root, path);
                var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(AgentSourceMigrationProjectChanges.ReadMigrationSourceText(path)));
                var bindings = declarations.Bindings
                    .Where(binding => ResolveDeclarationPath(binding) == relativePath)
                    .ToArray();
                var providers = declarations.Providers
                    .Where(provider => MetadataEquals(provider, "ContractId", descriptor.ContractId)
                                       && MetadataEquals(provider, "ContractVersion", descriptor.Version))
                    .ToArray();
                var uses = declarations.Uses
                    .Where(use => use.Include == descriptor.ContractId
                                  && MetadataEquals(use, "ContractVersion", descriptor.Version))
                    .ToArray();
                var bundles = declarations.Bundles
                    .Where(bundle => MetadataEquals(bundle, "ContractId", descriptor.ContractId)
                                     && MetadataEquals(bundle, "Version", descriptor.Version))
                    .ToArray();
                var audiences = providers
                    .Select(static provider => provider.Metadata.GetValueOrDefault("Audience"))
                    .Where(static audience => !string.IsNullOrWhiteSpace(audience))
                    .Select(static audience => audience!)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var exposure = relativePath.StartsWith(publicRoot, StringComparison.Ordinal) ? "public" : "package";
                ContractMigrationPolicy? migrationPolicy = null;
                if (exposure == "public")
                {
                    Assert.True(
                        ContractMigrationPolicies.TryGetValue(descriptor.ContractId, out migrationPolicy),
                        $"Public contract '{descriptor.ContractId}' has no reviewed H.3 migration policy.");
                    Assert.Equal(
                        migrationPolicy!.ExpectedProviders,
                        providers
                            .Select(static provider => new ProviderOwnership(
                                provider.Include,
                                provider.OwnerPackageId ?? string.Empty))
                            .OrderBy(static provider => provider.ProviderId, StringComparer.Ordinal));
                }
                var finalBundlePackageIds = providers
                    .Concat(uses)
                    .Select(static declaration => declaration.OwnerPackageId)
                    .Where(static packageId => !string.IsNullOrWhiteSpace(packageId))
                    .Select(static packageId => packageId!)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();

                return new DescriptorSnapshot(
                    relativePath,
                    descriptor.ContractId,
                    descriptor.DescriptorVersion,
                    descriptor.Version,
                    descriptor.SchemaProfile,
                    Convert.ToBase64String(descriptor.GetCanonicalUtf8()),
                    descriptor.Sha256,
                    Convert.ToBase64String(descriptor.GetCanonicalAbiProjectionUtf8()),
                    descriptor.AbiSha256,
                    exposure,
                    migrationPolicy,
                    audiences,
                    finalBundlePackageIds,
                    descriptor.Services.Select(service => new ServiceSnapshot(
                        service.ServiceId,
                        service.Methods.Select(method => new MethodSnapshot(
                            method.MethodId,
                            method.Kind == SunderRpcMethodKind.Unary ? "unary" : "server-stream",
                            method.Idempotency == SunderRpcMethodIdempotency.Idempotent ? "idempotent" : "none",
                            method.RequestSchemaReference,
                            method.OutputSchemaReference,
                            method.Errors)).ToArray())).ToArray(),
                    descriptor.Errors.Values
                        .OrderBy(static error => error.Code, StringComparer.Ordinal)
                        .Select(static error => new ErrorSnapshot(error.Code, error.Description, error.DetailsSchema))
                        .ToArray(),
                    bindings,
                    providers,
                    uses,
                    bundles);
            })
            .OrderBy(static descriptor => descriptor.ContractId, StringComparer.Ordinal)
            .ThenBy(static descriptor => descriptor.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<SourceMigrationSnapshot> ReadMigrationSources(string root)
        => Boundaries
            .SelectMany(boundary => AgentSourceMigrationProjectChanges.EnumerateMigrationHistoryFiles(root,
                    Path.Combine(root, NormalizePath(boundary.Root)), "*")
                .Where(AgentPackageRepositoryInventory.IsSourceFile)
                .Where(path => RelativePath(root, path) != AgentSourceMigrationProjectChanges.ObserverCompatibilitySource)
                .Select(path => ClassifyMigrationSource(root, boundary, path))
                .Concat(boundary.Id == "central" ? AgentSourceMigrationProjectChanges.ReviewedCentralRelocations.Select(move =>
                    ClassifyMigrationSource(root, boundary, Path.Combine(root, move.Value), move.Key)) : boundary.Id == "semantic-memory" ? new[]
                {
                    ("Protocol/Models/SemanticEmbeddingContext.cs", "Models/SemanticEmbeddingContext.cs"),
                    ("Protocol/Models/SemanticMemoryInspectorModels.cs", "Models/SemanticMemoryInspectorModels.cs"),
                    ("Protocol/Models/SemanticMemoryServiceModels.cs", "Models/SemanticMemoryServiceModels.cs"),
                    ("Protocol/MemoryInspectorContracts.cs", "Runtime/MemoryInspectorContracts.cs"),
                }.Select(move => ClassifyMigrationSource(root, boundary,
                    Path.Combine(root, "src/Sunder.Package.Agent.Memory.Semantic", move.Item2), "src/Sunder.Package.Agent.Memory.Semantic/" + move.Item1)) : boundary.Id == "skills" ? new[]
                {
                    ("Protocol/InstalledSkillRecord.cs", "Services/InstalledSkillRecord.cs"),
                    ("Protocol/SkillRuntimeAbstractions.cs", "Runtime/SkillRuntimeAbstractions.cs"),
                    ("Protocol/SkillRuntimeContracts.cs", "Runtime/SkillRuntimeContracts.cs"),
                }.Select(move => ClassifyMigrationSource(root, boundary, Path.Combine(root, "src/Sunder.Package.Agent.Skills", move.Item2), "src/Sunder.Package.Agent.Skills/" + move.Item1)) : boundary.Id == "subagents" ? new[]
                {
                    ("Protocol/SubagentRecord.cs", "Services/SubagentRecord.cs"),
                    ("Protocol/SubagentRuntimeContracts.cs", "Runtime/SubagentRuntimeContracts.cs"),
                    ("Protocol/TranscriptPageCursor.cs", "Runtime/TranscriptPageCursor.cs"),
                }.Select(move => ClassifyMigrationSource(root, boundary, Path.Combine(root, "src/Sunder.Package.Agent.Subagents", move.Item2), "src/Sunder.Package.Agent.Subagents/" + move.Item1)) : boundary.Id == "mcp" ? new[]
                {
                    ("Shared/McpManagementModels.cs", "Runtime/McpManagementModels.cs"),
                    ("Shared/McpManagementPayload.cs", "Runtime/McpManagementPayload.cs"),
                    ("Shared/McpManagementServiceModels.cs", "Services/McpManagementServiceModels.cs"),
                    ("Shared/McpServerName.cs", "Services/McpServerName.cs"),
                }.Select(move => ClassifyMigrationSource(root, boundary, Path.Combine(root, "src/Sunder.Package.Agent.Mcp", move.Item2), "src/Sunder.Package.Agent.Mcp/" + move.Item1)) : boundary.Id == "openai" ? new[]
                {
                    ("Shared/OpenAiManagementModels.cs", "Management/OpenAiManagementModels.cs"),
                    ("Shared/OpenAiUtilityModelSelection.cs", "Management/OpenAiUtilityModelSelection.cs"),
                }.Select(move => ClassifyMigrationSource(root, boundary, Path.Combine(root, "src/Sunder.Package.Agent.Provider.OpenAI", move.Item2), "src/Sunder.Package.Agent.Provider.OpenAI/" + move.Item1)) : boundary.Id == "builder" ? new[]
                {
                    ("Protocol/BuilderRpcContracts.cs", "Runtime/BuilderRpcContracts.cs"),
                    ("Protocol/BuilderRpcExecutionTargetProxy.cs", "App/Runtime/BuilderRpcExecutionTargetProxy.cs"),
                }.Select(move => ClassifyMigrationSource(root, boundary, Path.Combine(root, "src/Sunder.Package.Agent.Builder", move.Item2), "src/Sunder.Package.Agent.Builder/" + move.Item1))
                : boundary.Id == "agent-owner" ? AgentAppSourceMigrationTests.RelocatedModels.Select(path => ClassifyMigrationSource(root, boundary,
                    Path.Combine(root, path), "src/Sunder.Package.Agent/OwnerProtocol/" + Path.GetFileName(path))) : []))
            .OrderBy(static source => source.Path, StringComparer.Ordinal)
            .ToArray();

    private static SourceMigrationSnapshot ClassifyMigrationSource(
        string root,
        MigrationBoundary boundary,
        string path,
        string? originalPath = null)
    {
        var relativePath = originalPath ?? RelativePath(root, path);
        SourceRelocationPolicy policy;

        if (relativePath.EndsWith(".rpc.json", StringComparison.Ordinal))
        {
            policy = new("compatibility-corpus-then-delete", $"{BaselineRelativePath}#descriptors");
        }
        else if (IsProjectSupport(relativePath))
        {
            policy = new(
                "delete-helper-project-support",
                boundary.Id == "central"
                    ? "compatibility release evidence where verified; remove Sunder.Package.Agent.Contracts from source and solution"
                    : "canonical owner project; remove helper project, solution entry, and helper release lock");
        }
        else if (relativePath.EndsWith("AssemblyInfo.cs", StringComparison.Ordinal)
                 || relativePath.EndsWith("InternalsVisibleTo.cs", StringComparison.Ordinal))
        {
            policy = new(
                "delete-friend-assembly-workaround",
                "generated role-local source without helper-assembly access");
        }
        else if (ExplicitSourceRelocations.TryGetValue(relativePath, out var explicitPolicy))
        {
            policy = explicitPolicy;
        }
        else if (relativePath.EndsWith(".cs", StringComparison.Ordinal) && boundary.Id == "central")
        {
            policy = MapCentralBusinessSource(relativePath);
        }
        else
        {
            throw new InvalidDataException(
                $"Phase 0 RPC migration source '{relativePath}' needs an explicit reviewed disposition.");
        }

        return new SourceMigrationSnapshot(
            relativePath,
            HashSourceFile(path),
            boundary.Id,
            policy.Disposition,
            policy.Destination,
            policy.ContractIds,
            "Phase 5");
    }

    private static SourceRelocationPolicy MapCentralBusinessSource(string relativePath)
    {
        const string centralRoot = "src/Sunder.Package.Agent.Contracts/";
        var suffix = relativePath[centralRoot.Length..];
        var separator = suffix.IndexOf('/');
        if (separator < 0)
        {
            throw new InvalidDataException(
                $"Central RPC migration source '{relativePath}' needs an explicit reviewed destination.");
        }

        var area = suffix[..separator];
        var areaSuffix = suffix[(separator + 1)..];
        var destination = area switch
        {
            "Execution" => $"src/Sunder.Package.Agent/Models/Execution/{areaSuffix}",
            "Extensions" => $"src/Sunder.Package.Agent/Models/Extensions/{areaSuffix}",
            "Models" => $"src/Sunder.Package.Agent/Models/{areaSuffix}",
            "Providers" => $"src/Sunder.Package.Agent/Models/Providers/{areaSuffix}",
            "Runtime" => $"src/Sunder.Package.Agent/Runtime/{areaSuffix}",
            "Tools" => $"src/Sunder.Package.Agent/Models/Tools/{areaSuffix}",
            _ => throw new InvalidDataException(
                $"Central RPC migration source '{relativePath}' needs an explicit reviewed destination."),
        };
        return new SourceRelocationPolicy("migrate-base-agent-semantic-source", destination);
    }

    private static BuildTopologySnapshot ReadBuildTopology(
        string root,
        DeclarationInventory declarations)
    {
        var msbuildSources = EnumerateRepositoryMsBuildSources(root);
        var solutionEntries = XDocument.Load(Path.Combine(root, "Sunder.AgentPackage.slnx"))
            .Descendants("Project")
            .Select(static project => NormalizeRelativePath(project.Attribute("Path")?.Value ?? string.Empty))
            .Where(static path => path.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var projects = msbuildSources
            .Where(static path => Path.GetExtension(path) == ".csproj")
            .Select(path =>
            {
                var relativePath = RelativePath(root, path);
                var document = AgentSourceMigrationProjectChanges.ReadMsBuildHistoryDocument(root, path);
                var disposition = IsHelperBoundaryPath(relativePath)
                    ? "delete-rpc-helper-project-after-source-relocation"
                    : relativePath.StartsWith("tests/", StringComparison.Ordinal)
                        ? "retain-test-or-fixture-project"
                        : relativePath.StartsWith("scripts/", StringComparison.Ordinal)
                            ? "retain-build-or-release-tool-project"
                            : "retain-reviewed-business-project";
                return new ProjectSnapshot(
                    relativePath,
                    HashProjectBeforeMigrationAdditions(root, path),
                    ReadProjectProperty(document, "SunderPackageFamilyId") ?? InferOwnerPackageId(relativePath),
                    ReadProjectProperty(document, "SunderPackageRole")
                    ?? (IsHelperBoundaryPath(relativePath) ? "rpc-helper" : null),
                    AgentSourceMigrationProjectChanges.HistoricalSolutionMembership(relativePath, solutionEntries.Contains(relativePath)),
                    disposition,
                    IsHelperBoundaryPath(relativePath) ? "Phase 5" : "retained");
            })
            .OrderBy(static project => project.Path, StringComparer.Ordinal)
            .ToArray();
        Assert.All(solutionEntries, entry => Assert.Contains(projects, project => project.Path == entry));

        var generatedOutputs = declarations.Bindings
            .Select(binding =>
            {
                var outputFile = binding.Metadata.GetValueOrDefault("OutputFile")
                    ?? throw new InvalidDataException(
                        $"RPC binding '{binding.Project}:{binding.Include}' has no OutputFile.");
                var projectDirectory = Path.GetDirectoryName(binding.Project)?.Replace('\\', '/') ?? string.Empty;
                return new GeneratedOutputSnapshot(
                    binding.Project,
                    binding.OwnerPackageId,
                    binding.Role,
                    ResolveDeclarationPath(binding),
                    $"{projectDirectory}/obj/$(Configuration)/$(TargetFramework)/sunder-rpc/{outputFile}",
                    "delete-descriptor-first-generated-output",
                    "Phase 5");
            })
            .OrderBy(static output => output.Project, StringComparer.Ordinal)
            .ThenBy(static output => output.DescriptorPath, StringComparer.Ordinal)
            .ToArray();

        var adapterSources = AgentSourceMigrationProjectChanges.EnumerateMigrationHistoryFiles(root, Path.Combine(root, "src"), "*.cs")
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Select(path =>
            {
                var relative = RelativePath(root, path);
                var semanticOriginal = AgentSourceMigrationProjectChanges.ReviewedCentralRelocations.FirstOrDefault(move => move.Value == relative).Key;
                if (semanticOriginal is not null) relative = semanticOriginal;
                if (relative == "src/Sunder.Package.Agent/App/Runtime/AgentOwnerRpcClient.Legacy.cs")
                    relative = "src/Sunder.Package.Agent/Runtime/AgentAppRuntimeGateway.Transport.cs";
                // This activation bridge has migrated away from Protocol dependencies.
                // Preserve its exact original bytes as the historical source oracle;
                // generated composition and lifecycle tests verify the replacement.
                var original = relative switch
                {
                    "src/Sunder.Package.Agent/Runtime/DeferredAgentRpcServiceHandler.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/DeferredAgentRpcServiceHandler.before-source.cs.txt"),
                    "src/Sunder.Package.Agent/Program.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/AgentWorkerProgram.before-source.cs.txt"),
                    "src/Sunder.Package.Agent.Memory.Semantic/Program.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/MemoryWorkerProgram.before-source.cs.txt"),
                    "src/Sunder.Package.Agent.Memory.Semantic/App/MemoryAppRuntimeGateway.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/MemoryAppRuntimeGateway.before-source.cs.txt"),
                    "src/Sunder.Package.Agent.Skills/Program.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/SkillsWorkerProgram.before-source.cs.txt"),
                    "src/Sunder.Package.Agent.Subagents/Program.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/SubagentsWorkerProgram.before-source.cs.txt"),
                    "src/Sunder.Package.Agent.Mcp/Worker/McpWorkerConfiguration.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/McpWorkerConfiguration.before-source.cs.txt"),
                    "src/Sunder.Package.Agent.Execution.Local/Program.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/LocalWorkerProgram.before-source.cs.txt"),
                    "src/Sunder.Package.Agent.Execution.Docker/Program.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/DockerWorkerProgram.before-source.cs.txt"),
                    "src/Sunder.Package.Agent.Provider.Gemini/Program.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/GeminiWorkerProgram.before-source.cs.txt"),
                    "src/Sunder.Package.Agent.Provider.Anthropic/Program.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/AnthropicWorkerProgram.before-source.cs.txt"),
                    "src/Sunder.Package.Agent.Provider.LMStudio/Program.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/LMStudioWorkerProgram.before-source.cs.txt"),
                    "src/Sunder.Package.Agent.Provider.OpenAI/Worker/OpenAiWorkerConfiguration.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/OpenAiWorkerConfiguration.before-source.cs.txt"),
                    "src/Sunder.Package.Agent.Tools.Files/Program.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/FilesWorkerProgram.before-source.cs.txt"),
                    "src/Sunder.Package.Agent.Tools.Shell/Program.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/ShellWorkerProgram.before-source.cs.txt"),
                    "src/Sunder.Package.Agent.Tools.Web/Program.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/WebWorkerProgram.before-source.cs.txt"),
                    "src/Sunder.Package.Agent.Builder/Program.cs" => Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures/BuilderWorkerProgram.before-source.cs.txt"),
                    _ => path,
                };
                var text = AgentSourceMigrationProjectChanges.ReadMigrationSourceText(original);
                text = AgentSourceMigrationProjectChanges.BeforeUiFollowup(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeAgentComposition(original, text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
                text = AgentSourceMigrationProjectChanges.BeforeSubagentRuntimeBindings(original, text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
                text = AgentSourceMigrationProjectChanges.BeforeMcpRelocation(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeLocalRelocation(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeOpenAiRelocation(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeFilesBindings(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeShellBindings(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeWebComposition(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeProviderComposition(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeSkillsComposition(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeMemoryComposition(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeDockerComposition(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeOpenAiComposition(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeBuilderRelocation(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeOwnerRuntimeModels(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeAgentEmbeddingBindings(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeAgentChatBindings(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeAgentBehaviorBindings(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeAgentWorkspaceEditorBindings(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeAgentSemanticSeparation(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeProviderChatModels(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeProviderCatalogSeparation(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeTranscriptPresentationSeparation(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeExecutionFacetSeparation(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeAgentCleanupBindings(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeAgentMetadataBindings(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeAgentSelectableBindings(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeAgentWorkspacePathBindings(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeAgentToolProfileBindings(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeAgentPromptContextBindings(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeAgentExecutionBindings(original, text);
                text = AgentSourceMigrationProjectChanges.BeforeAgentToolBindings(original, text);
                if (relative == "src/Sunder.Package.Agent.Memory.Semantic/Services/SemanticModelRuntimeResolver.cs")
                    text = AgentSourceMigrationProjectChanges.BeforeMemoryResolverPort(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
                if (relative == "src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeGateway.cs") text = AgentSourceMigrationProjectChanges.ReconstructSkillsGateway(original);
                if (relative == "src/Sunder.Package.Agent.Subagents/Runtime/SubagentOwnerRpc.cs") text = AgentSourceMigrationProjectChanges.ReconstructSubagentsOwnerRpc(original);
                if (relative == "src/Sunder.Package.Agent.Skills/Services/SkillsFeature.cs")
                    text = AgentSourceMigrationProjectChanges.BeforeSkillsRuntimePort(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
                return (Path: original, RelativePath: relative, Text: text);
            })
            .Where(static source => !IsHelperBoundaryPath(source.RelativePath))
            .Where(static source => IsRpcAdapterSource(source.Text))
            .Select(static source => new RpcAdapterSourceSnapshot(
                source.RelativePath,
                HashSourceFile(source.Path),
                InferOwnerPackageId(source.RelativePath),
                ClassifyRpcAdapterSource(source.Text),
                source.RelativePath,
                "Phase 5"))
            .OrderBy(static source => source.Path, StringComparer.Ordinal)
            .ToArray();

        var linkedSources = ReadMsBuildDeclarations(root, msbuildSources, "Compile")
            .Where(static declaration => declaration.Metadata.ContainsKey("Link")).ToArray();
        Assert.Equal(62, linkedSources.Count(declaration => AgentSourceMigrationProjectChanges.IsAddedLink(declaration.Project, declaration.Include)));
        return new BuildTopologySnapshot(
            projects,
            ReadMsBuildDeclarations(root, msbuildSources, "ProjectReference"),
            linkedSources.Where(declaration => !AgentSourceMigrationProjectChanges.IsAddedLink(declaration.Project, declaration.Include))
                .ToArray(),
            generatedOutputs,
            adapterSources);
    }

    private static IReadOnlyList<string> EnumerateRepositoryMsBuildSources(string root)
    {
        var sourceRoots = new[] { "src", "tests", "scripts" }
            .Select(relativePath => Path.Combine(root, relativePath))
            .Where(Directory.Exists);
        return sourceRoots
            .SelectMany(path => AgentSourceMigrationProjectChanges.EnumerateMigrationHistoryFiles(root, path, "*"))
            .Concat(Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            .Where(AgentPackageRepositoryInventory.IsSourceFile)
            .Where(static path => Path.GetExtension(path) is ".csproj" or ".props" or ".targets")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsRpcAdapterSource(string source)
        => source.Contains("Sunder.Sdk.Rpc", StringComparison.Ordinal)
           || source.Contains("Sunder.Sdk.Services", StringComparison.Ordinal)
           || source.Contains("Protocol.Generated", StringComparison.Ordinal)
           || source.Contains("AgentRpcCatalog", StringComparison.Ordinal)
           || source.Contains("ISunderRpc", StringComparison.Ordinal)
           || source.Contains("SunderRpc", StringComparison.Ordinal);

    private static string ClassifyRpcAdapterSource(string source)
    {
        if (source.Contains("DescriptorSha256", StringComparison.Ordinal)
            || source.Contains("AbiSha256", StringComparison.Ordinal))
        {
            return "retain-composition-replace-manual-rpc-registration";
        }
        if (source.Contains("Protocol.Generated", StringComparison.Ordinal))
        {
            return "retain-semantic-adapter-replace-generated-transport-dependency";
        }
        if (source.Contains("AgentRpcCatalog", StringComparison.Ordinal))
        {
            return "retain-business-source-replace-central-catalog-with-role-local-generated-client";
        }
        return "retain-business-source-replace-manual-rpc-integration";
    }

    private static bool IsHelperBoundaryPath(string relativePath)
        => Boundaries.Any(boundary =>
            relativePath.StartsWith(boundary.Root + "/", StringComparison.Ordinal));

    private static string? InferOwnerPackageId(string relativePath)
    {
        if (!relativePath.StartsWith("src/", StringComparison.Ordinal))
        {
            return null;
        }

        var separator = relativePath.IndexOf('/', "src/".Length);
        var projectRoot = separator < 0 ? relativePath["src/".Length..] : relativePath["src/".Length..separator];
        return projectRoot switch
        {
            "Sunder.Package.Agent" or "Sunder.Package.Agent.Contracts" => "sunder.package.agent",
            "Sunder.Package.Agent.Builder" => "sunder.package.agent.builder",
            "Sunder.Package.Agent.Execution.Docker" => "sunder.package.agent.execution.docker",
            "Sunder.Package.Agent.Execution.Local" => "sunder.package.agent.execution.local",
            "Sunder.Package.Agent.Mcp" => "sunder.package.agent.mcp",
            "Sunder.Package.Agent.Memory.Semantic" => "sunder.package.agent.memory.semantic",
            "Sunder.Package.Agent.Provider.Anthropic" => "sunder.package.agent.provider.anthropic",
            "Sunder.Package.Agent.Provider.Gemini" => "sunder.package.agent.provider.gemini",
            "Sunder.Package.Agent.Provider.LMStudio" => "sunder.package.agent.provider.lmstudio",
            "Sunder.Package.Agent.Provider.OpenAI" => "sunder.package.agent.provider.openai",
            "Sunder.Package.Agent.Skills" => "sunder.package.agent.skills",
            "Sunder.Package.Agent.Subagents" => "sunder.package.agent.subagents",
            "Sunder.Package.Agent.Tools.Files" => "sunder.package.agent.tools.files",
            "Sunder.Package.Agent.Tools.Shell" => "sunder.package.agent.tools.shell",
            "Sunder.Package.Agent.Tools.Web" => "sunder.package.agent.tools.web",
            _ => null,
        };
    }

    private static bool IsProjectSupport(string relativePath)
        => relativePath.EndsWith(".csproj", StringComparison.Ordinal)
           || relativePath.EndsWith("packages.release.lock.json", StringComparison.Ordinal)
           || relativePath.EndsWith("PublicAPI.Shipped.txt", StringComparison.Ordinal)
           || relativePath.EndsWith("PublicAPI.Unshipped.txt", StringComparison.Ordinal)
           || relativePath.EndsWith("/README.md", StringComparison.Ordinal)
           || relativePath.Contains("/buildTransitive/", StringComparison.Ordinal);

    private static string ResolveDeclarationPath(MsBuildDeclaration declaration)
    {
        var projectDirectory = Path.GetDirectoryName(declaration.Project)?.Replace('\\', '/') ?? string.Empty;
        return NormalizeRelativePath(Path.Combine(projectDirectory, declaration.Include));
    }

    private static bool MetadataEquals(MsBuildDeclaration declaration, string name, string expected)
        => declaration.Metadata.TryGetValue(name, out var actual)
           && string.Equals(actual, expected, StringComparison.Ordinal);

    private static string RelativePath(string root, string path)
        => NormalizeRelativePath(Path.GetRelativePath(root, path));

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static string NormalizePath(string path)
        => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static string HashSourceFile(string path)
    {
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent.Mcp/Runtime/McpManagementPayload.cs", StringComparison.Ordinal))
            path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../../tests/Sunder.Package.Agent.Tests/Fixtures/McpManagementPayload.before-source.cs.txt"));
        var text = AgentSourceMigrationProjectChanges.ReadMigrationSourceText(path);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        normalized = AgentSourceMigrationProjectChanges.BeforeUiFollowup(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeCentralInterfaceRelocation(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeAgentComposition(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeSubagentRuntimeBindings(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeMcpRelocation(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeLocalRelocation(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeOpenAiRelocation(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeFilesBindings(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeShellBindings(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeWebComposition(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeProviderComposition(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeSkillsComposition(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeMemoryComposition(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeDockerComposition(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeOpenAiComposition(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeBuilderRelocation(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeOwnerRuntimeModels(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeAgentEmbeddingBindings(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeAgentChatBindings(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeAgentBehaviorBindings(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeAgentWorkspaceEditorBindings(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeAgentSemanticSeparation(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeProviderChatModels(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeProviderCatalogSeparation(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeTranscriptPresentationSeparation(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeExecutionFacetSeparation(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeAgentCleanupBindings(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeAgentMetadataBindings(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeAgentSelectableBindings(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeAgentWorkspacePathBindings(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeAgentToolProfileBindings(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeAgentPromptContextBindings(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeAgentExecutionBindings(path, normalized);
        normalized = AgentSourceMigrationProjectChanges.BeforeAgentToolBindings(path, normalized);
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent.Contracts/Sunder.Package.Agent.Contracts.csproj", StringComparison.Ordinal))
            normalized = AgentSourceMigrationProjectChanges.BeforeAdditions(AgentPackageRepositoryInventory.RepositoryRoot.FullName, path, normalized);
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent/OwnerProtocol/Sunder.Package.Agent.OwnerProtocol.csproj", StringComparison.Ordinal))
            normalized = AgentSourceMigrationProjectChanges.BeforeAdditions(AgentPackageRepositoryInventory.RepositoryRoot.FullName, path, normalized);
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent.Builder/Protocol/Sunder.Package.Agent.Builder.Protocol.csproj", StringComparison.Ordinal))
            normalized = AgentSourceMigrationProjectChanges.BeforeAdditions(AgentPackageRepositoryInventory.RepositoryRoot.FullName, path, normalized);
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent.Provider.OpenAI/Shared/Sunder.Package.Agent.Provider.OpenAI.Shared.csproj", StringComparison.Ordinal))
            normalized = AgentSourceMigrationProjectChanges.BeforeAdditions(AgentPackageRepositoryInventory.RepositoryRoot.FullName, path, normalized);
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent.Contracts/Rpc/AgentRpcWire.cs", StringComparison.Ordinal))
        {
            const string unorderedDiscriminators = "        AllowOutOfOrderMetadataProperties = true,\n";
            Assert.Equal(2, normalized.Split(unorderedDiscriminators, StringSplitOptions.None).Length);
            normalized = normalized.Replace(unorderedDiscriminators, string.Empty, StringComparison.Ordinal);
        }
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent.Execution.Local/Protocol/LocalProtocol.csproj", StringComparison.Ordinal))
            normalized = AgentSourceMigrationProjectChanges.BeforeAdditions(AgentPackageRepositoryInventory.RepositoryRoot.FullName, path, normalized);
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent.Execution.Docker/Protocol/DockerProtocol.csproj", StringComparison.Ordinal))
            normalized = AgentSourceMigrationProjectChanges.BeforeAdditions(AgentPackageRepositoryInventory.RepositoryRoot.FullName, path, normalized);
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent.Mcp/Shared/Sunder.Package.Agent.Mcp.Shared.csproj", StringComparison.Ordinal))
            normalized = AgentSourceMigrationProjectChanges.BeforeAdditions(AgentPackageRepositoryInventory.RepositoryRoot.FullName, path, normalized);
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent.Memory.Semantic/Runtime/MemoryInspectorContracts.cs", StringComparison.Ordinal))
            normalized = AgentSourceMigrationProjectChanges.ReconstructMemoryInspectorContracts(path);
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent.Memory.Semantic/Protocol/Sunder.Package.Agent.Memory.Semantic.Protocol.csproj", StringComparison.Ordinal))
            normalized = AgentSourceMigrationProjectChanges.BeforeAdditions(AgentPackageRepositoryInventory.RepositoryRoot.FullName, path, normalized);
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent.Skills/Protocol/SkillsProtocol.csproj", StringComparison.Ordinal))
            normalized = AgentSourceMigrationProjectChanges.BeforeAdditions(AgentPackageRepositoryInventory.RepositoryRoot.FullName, path, normalized);
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent.Subagents/Protocol/SubagentsProtocol.csproj", StringComparison.Ordinal))
            normalized = AgentSourceMigrationProjectChanges.BeforeAdditions(AgentPackageRepositoryInventory.RepositoryRoot.FullName, path, normalized);
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent.Subagents/Runtime/SubagentOwnerRpc.cs", StringComparison.Ordinal))
            normalized = AgentSourceMigrationProjectChanges.ReconstructSubagentsOwnerRpc(path);
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeGateway.cs", StringComparison.Ordinal))
            normalized = AgentSourceMigrationProjectChanges.ReconstructSkillsGateway(path);
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent.Memory.Semantic/Services/SemanticModelRuntimeResolver.cs", StringComparison.Ordinal))
            normalized = AgentSourceMigrationProjectChanges.BeforeMemoryResolverPort(normalized);
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent.Skills/Services/SkillsFeature.cs", StringComparison.Ordinal))
            normalized = AgentSourceMigrationProjectChanges.BeforeSkillsRuntimePort(normalized);
        if (NormalizeRelativePath(path).EndsWith("/src/Sunder.Package.Agent/OwnerProtocol/AgentOwnerRuntimeRpc.cs", StringComparison.Ordinal))
        {
            // This exact interface moved into the App assembly. Reconstruct the
            // historical preimage so the frozen hash still checks every original byte.
            var client = File.ReadAllText(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName,
                    "tests/Sunder.Package.Agent.Tests/Fixtures/OrdinaryServices/IAgentOwnerClient.cs.txt"))
                .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            Assert.Equal("fde0397c933ffe1383d3bafbc2fad1b3cbca80b04daa68021cfcf9c69326ab6f",
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(client))));
            var declaration = client[client.IndexOf("internal interface IAgentOwnerClient", StringComparison.Ordinal)..].TrimEnd();
            const string marker = "internal interface IAgentOwnerRuntimeProvider";
            Assert.Equal(2, normalized.Split(marker, StringSplitOptions.None).Length);
            normalized = normalized.Replace(marker, declaration + "\n\n" + marker, StringComparison.Ordinal);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static string HashProjectBeforeMigrationAdditions(string root, string path)
    {
        var text = File.Exists(path) ? AgentSourceMigrationProjectChanges.ReadNormalizedProjectText(path) : string.Empty;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            AgentSourceMigrationProjectChanges.BeforeAdditions(root, path, text))));
    }

    private sealed record RpcMigrationBaseline(
        int SchemaVersion,
        string Purpose,
        string SourceHashPolicy,
        IReadOnlyList<DescriptorSnapshot> Descriptors,
        IReadOnlyList<MsBuildDeclaration> ExternalContractProviders,
        IReadOnlyList<MsBuildDeclaration> ExternalContractUses,
        IReadOnlyList<MsBuildDeclaration> ExternalContractBundles,
        BuildTopologySnapshot BuildTopology,
        IReadOnlyList<SourceMigrationSnapshot> SourceRelocations);

    private sealed record DescriptorSnapshot(
        string Path,
        string ContractId,
        int DescriptorVersion,
        string Version,
        string SchemaProfile,
        string CanonicalDescriptorBase64,
        string DescriptorSha256,
        string CanonicalAbiProjectionBase64,
        string AbiSha256,
        string Exposure,
        ContractMigrationPolicy? MigrationPolicy,
        IReadOnlyList<string> Audiences,
        IReadOnlyList<string> FinalBundlePackageIds,
        IReadOnlyList<ServiceSnapshot> Services,
        IReadOnlyList<ErrorSnapshot> Errors,
        IReadOnlyList<MsBuildDeclaration> Bindings,
        IReadOnlyList<MsBuildDeclaration> Providers,
        IReadOnlyList<MsBuildDeclaration> Uses,
        IReadOnlyList<MsBuildDeclaration> Bundles);

    private sealed record ServiceSnapshot(string ServiceId, IReadOnlyList<MethodSnapshot> Methods);

    private sealed record MethodSnapshot(
        string MethodId,
        string Kind,
        string Idempotency,
        string RequestSchemaReference,
        string OutputSchemaReference,
        IReadOnlyList<string> Errors);

    private sealed record ErrorSnapshot(string Code, string? Description, string? DetailsSchema);

    private sealed record ContractMigrationPolicy(
        string SourceClassification,
        string FinalSourceLocation,
        string AuthorityKind,
        string AuthorityId,
        string ProviderPublicationPolicy,
        IReadOnlyList<string> ProviderPackageAllowlist,
        IReadOnlyList<ProviderOwnership> ExpectedProviders);

    private sealed record ProviderOwnership(string ProviderId, string PackageId);

    private sealed record MsBuildDeclaration(
        string Project,
        string? OwnerPackageId,
        string? Role,
        string? Condition,
        string Include,
        IReadOnlyDictionary<string, string> Metadata);

    private sealed record DeclarationInventory(
        IReadOnlyList<MsBuildDeclaration> Bindings,
        IReadOnlyList<MsBuildDeclaration> Providers,
        IReadOnlyList<MsBuildDeclaration> Uses,
        IReadOnlyList<MsBuildDeclaration> Bundles);

    private sealed record BuildTopologySnapshot(
        IReadOnlyList<ProjectSnapshot> Projects,
        IReadOnlyList<MsBuildDeclaration> ProjectReferences,
        IReadOnlyList<MsBuildDeclaration> LinkedSourceDeclarations,
        IReadOnlyList<GeneratedOutputSnapshot> GeneratedOutputs,
        IReadOnlyList<RpcAdapterSourceSnapshot> RpcAdapterSources);

    private sealed record ProjectSnapshot(
        string Path,
        string Sha256,
        string? PackageId,
        string? Role,
        bool InSolution,
        string Disposition,
        string OwningPhase);

    private sealed record GeneratedOutputSnapshot(
        string Project,
        string? OwnerPackageId,
        string? Role,
        string DescriptorPath,
        string CurrentOutputLocation,
        string Disposition,
        string OwningPhase);

    private sealed record RpcAdapterSourceSnapshot(
        string Path,
        string Sha256,
        string? OwnerPackageId,
        string Disposition,
        string Destination,
        string OwningPhase);

    private sealed record MigrationBoundary(string Id, string Root);

    private sealed record SourceRelocationPolicy(
        string Disposition,
        string Destination,
        IReadOnlyList<string>? ContractIds = null);

    private sealed record SourceMigrationSnapshot(
        string Path,
        string Sha256,
        string Boundary,
        string Disposition,
        string Destination,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? ContractIds,
        string OwningPhase);
}
