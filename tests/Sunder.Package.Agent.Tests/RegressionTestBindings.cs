namespace Sunder.Package.Agent.Tests;

// Contract ids are scenario selection keys only. Descriptors, models, clients and
// provider registrations come from current reviewed artifacts/generated code.
internal static class RegressionTestBindings
{
    internal const string ChatProviders = "sunder.agent.chat.provider";
    internal const string EmbeddingProviders = "sunder.agent.embedding.provider";
    internal const string ExecutionTargets = "sunder.agent.execution.target";
    internal const string ToolSources = "sunder.agent.tool.source";
    internal const string ObservedToolExecutions = "sunder.agent.tool.execution.observed";
    internal const string SystemPromptContributors = "sunder.agent.system.prompt.contributor";
    internal const string PermissionSurfaces = "sunder.agent.permission.surface";
    internal const string ProfileCapabilityConsumers = "sunder.agent.profile.capability.consumer";
    internal const string SelectableCapabilityProviders = "sunder.agent.selectable.capability.provider";
    internal const string SessionCleaners = "sunder.agent.session.cleaner";
    internal const string DurableLifecycleObservers = "sunder.agent.durable.lifecycle.observer";
    internal const string PromptContextContributors = "sunder.agent.prompt.context.contributor";
    internal const string WorkspacePathMigrators = "sunder.agent.workspace.path.migrator";
    internal const string WorkspaceEditors = "sunder.agent.workspace.editor";
    internal const string RuntimeCatalog = "sunder.agent.runtime.catalog";
    internal const string WorkspaceExecutionResolver = "sunder.agent.workspace.execution.resolver";
    internal const string ChildRunExecutor = "sunder.agent.child.run.executor";
    internal const string BehaviorLoops = "sunder.agent.behavior.loop";
    internal const string RunControls = "sunder.agent.run.control";
}
