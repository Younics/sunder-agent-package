namespace Sunder.Package.Agent.Protocol;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public static class AgentRpcContractIds
{
    public const string ChatProvider = "sunder.agent.chat.provider";
    public const string EmbeddingProvider = "sunder.agent.embedding.provider";
    public const string RuntimeCatalog = "sunder.agent.runtime.catalog";
    public const string WorkspaceExecutionResolver = "sunder.agent.workspace.execution.resolver";
    public const string ChildRunExecutor = "sunder.agent.child.run.executor";
    public const string SessionCleaner = "sunder.agent.session.cleaner";
    public const string SystemPromptContributor = "sunder.agent.system.prompt.contributor";
    public const string ToolSource = "sunder.agent.tool.source";
    internal const string ObservedToolExecution = "sunder.agent.tool.execution.observed";
    public const string PermissionSurface = "sunder.agent.permission.surface";
    public const string PromptContextContributor = "sunder.agent.prompt.context.contributor";
    public const string DurableLifecycleObserver = "sunder.agent.durable.lifecycle.observer";
    public const string ProfileCapabilityConsumer = "sunder.agent.profile.capability.consumer";
    public const string SelectableCapabilityProvider = "sunder.agent.selectable.capability.provider";
    public const string BehaviorLoop = "sunder.agent.behavior.loop";
    public const string RunControl = "sunder.agent.run.control";
    public const string ExecutionTarget = "sunder.agent.execution.target";
    public const string WorkspacePathMigrator = "sunder.agent.workspace.path.migrator";
    public const string WorkspaceEditor = "sunder.agent.workspace.editor";
}
