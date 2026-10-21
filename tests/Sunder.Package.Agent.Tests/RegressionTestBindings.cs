namespace Sunder.Package.Agent.Tests;

// Contract ids are scenario selection keys only. Descriptors, models, clients and
// provider registrations come from current reviewed artifacts/generated code.
internal static class RegressionTestBindings
{
    internal const string ChatProviders = "younics.ai.agent.chat.provider";
    internal const string EmbeddingProviders = "younics.ai.agent.embedding.provider";
    internal const string ExecutionTargets = "younics.ai.agent.execution.target";
    internal const string ToolSources = "younics.ai.agent.tool.source";
    internal const string ObservedToolExecutions = "younics.ai.agent.tool.execution.observed";
    internal const string SystemPromptContributors = "younics.ai.agent.system.prompt.contributor";
    internal const string PermissionSurfaces = "younics.ai.agent.permission.surface";
    internal const string ProfileCapabilityConsumers = "younics.ai.agent.profile.capability.consumer";
    internal const string SelectableCapabilityProviders = "younics.ai.agent.selectable.capability.provider";
    internal const string SessionCleaners = "younics.ai.agent.session.cleaner";
    internal const string DurableLifecycleObservers = "younics.ai.agent.durable.lifecycle.observer";
    internal const string PromptContextContributors = "younics.ai.agent.prompt.context.contributor";
    internal const string WorkspaceEditors = "younics.ai.agent.workspace.editor";
    internal const string RuntimeCatalog = "younics.ai.agent.runtime.catalog";
    internal const string WorkspaceExecutionResolver = "younics.ai.agent.workspace.execution.resolver";
    internal const string ChildRunExecutor = "younics.ai.agent.child.run.executor";
    internal const string BehaviorLoops = "younics.ai.agent.behavior.loop";
    internal const string RunControls = "younics.ai.agent.run.control";
}
