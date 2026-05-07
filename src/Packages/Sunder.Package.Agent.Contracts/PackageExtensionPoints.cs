using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Contracts;

public static class PackageExtensionPoints
{
    public static readonly PackageExtensionPoint<IAgentChatProvider> ChatProviders =
        new("sunder.package.agent:chat-providers");

    public static readonly PackageExtensionPoint<IAgentBehaviorLoop> BehaviorLoops =
        new("sunder.package.agent:behavior-loops");

    public static readonly PackageExtensionPoint<IAgentEmbeddingProvider> EmbeddingProviders =
        new("sunder.package.agent:embedding-providers");

    public static readonly PackageExtensionPoint<IAgentProfileCapabilityConsumer> ProfileCapabilityConsumers =
        new("sunder.package.agent:profile-capability-consumers");

    public static readonly PackageExtensionPoint<IAgentProfileSelectableCapabilityProvider> ProfileSelectableCapabilityProviders =
        new("sunder.package.agent:profile-selectable-capability-providers");

    public static readonly PackageExtensionPoint<IAgentRuntimeCatalog> RuntimeCatalogs =
        new("sunder.package.agent:runtime-catalogs");

    public static readonly PackageExtensionPoint<IAgentChildRunExecutor> ChildRunExecutors =
        new("sunder.package.agent:child-run-executors");

    public static readonly PackageExtensionPoint<IAgentAttachmentContentStore> AttachmentContentStores =
        new("sunder.package.agent:attachment-content-stores");

    public static readonly PackageExtensionPoint<IAgentSessionDataCleaner> SessionDataCleaners =
        new("sunder.package.agent:session-data-cleaners");

    public static readonly PackageExtensionPoint<IAgentSystemPromptContributor> SystemPromptContributors =
        new("sunder.package.agent:system-prompt-contributors");

    public static readonly PackageExtensionPoint<IAgentTool> Tools =
        new("sunder.package.agent:tools");

    public static readonly PackageExtensionPoint<IAgentToolSource> ToolSources =
        new("sunder.package.agent:tool-sources");

    public static readonly PackageExtensionPoint<IAgentExecutionTarget> ExecutionTargets =
        new("sunder.package.agent:execution-targets");

    public static readonly PackageExtensionPoint<IAgentExecutionResourceProvider> ExecutionResourceProviders =
        new("sunder.package.agent:execution-resource-providers");

    public static readonly PackageExtensionPoint<IAgentWorkspaceBindingContributor> WorkspaceBindingContributors =
        new("sunder.package.agent:workspace-binding-contributors");

    public static readonly PackageExtensionPoint<IAgentWorkspaceEditorContributor> WorkspaceEditorContributors =
        new("sunder.package.agent:workspace-editor-contributors");

    public static readonly PackageExtensionPoint<IAgentProfileEditorContributor> ProfileEditorContributors =
        new("sunder.package.agent:profile-editor-contributors");

    public static readonly PackageExtensionPoint<IAgentPermissionSurface> PermissionSurfaces =
        new("sunder.package.agent:permission-surfaces");

    public static readonly PackageExtensionPoint<IAgentPromptContextContributor> PromptContextContributors =
        new("sunder.package.agent:prompt-context-contributors");

    public static readonly PackageExtensionPoint<IAgentLifecycleObserver> LifecycleObservers =
        new("sunder.package.agent:lifecycle-observers");

}
