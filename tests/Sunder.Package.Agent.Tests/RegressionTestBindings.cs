using System.Reflection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Tests;

// Named access to the real Worker consumer bindings for linked test sources.
// No client factory, dispatch table or contract identity is reimplemented here.
internal static class RegressionTestBindings
{
    internal static AgentRpcService<IAgentChatProvider> ChatProviders { get; } = Get<IAgentChatProvider>("AgentChatBindings");
    internal static AgentRpcService<IAgentEmbeddingProvider> EmbeddingProviders { get; } = Get<IAgentEmbeddingProvider>("AgentEmbeddingBindings");
    internal static AgentRpcService<IAgentExecutionTarget> ExecutionTargets { get; } = Get<IAgentExecutionTarget>("AgentExecutionBindings");
    internal static AgentRpcService<IAgentToolSource> ToolSources { get; } = Get<IAgentToolSource>("AgentToolBindings");
    internal static AgentRpcService<IAgentSystemPromptContributor> SystemPromptContributors { get; } = Get<IAgentSystemPromptContributor>("AgentMetadataBindings", "SystemPromptContributors");
    internal static AgentRpcService<IAgentPermissionSurface> PermissionSurfaces { get; } = Get<IAgentPermissionSurface>("AgentMetadataBindings", "PermissionSurfaces");
    internal static AgentRpcService<IAgentProfileCapabilityConsumer> ProfileCapabilityConsumers { get; } = Get<IAgentProfileCapabilityConsumer>("AgentMetadataBindings", "ProfileCapabilityConsumers");
    internal static AgentRpcService<IAgentProfileSelectableCapabilityProvider> SelectableCapabilityProviders { get; } = Get<IAgentProfileSelectableCapabilityProvider>("AgentSelectableBindings");
    internal static AgentRpcService<IAgentSessionDataCleaner> SessionCleaners { get; } = Get<IAgentSessionDataCleaner>("AgentCleanupBindings", "SessionCleaners");
    internal static AgentRpcService<IAgentDurableLifecycleObserver> DurableLifecycleObservers { get; } = Get<IAgentDurableLifecycleObserver>("AgentCleanupBindings", "DurableLifecycleObservers");
    internal static AgentRpcService<IAgentPromptContextContributor> PromptContextContributors { get; } = Get<IAgentPromptContextContributor>("AgentPromptContextBindings");
    internal static AgentRpcService<IAgentWorkspacePathMigrationContributor> WorkspacePathMigrators { get; } = Get<IAgentWorkspacePathMigrationContributor>("AgentWorkspacePathBindings");

    internal static ValueTask<AgentRpcReference<IAgentExecutionTarget>?> ResolveExecutionAsync(AgentRpcCatalog catalog, AgentRpcProviderHandle handle, CancellationToken token)
        => (ValueTask<AgentRpcReference<IAgentExecutionTarget>?>)typeof(AgentRpcCatalog)
            .GetMethod("TryGetServiceReferenceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(IAgentExecutionTarget)).Invoke(catalog, [ExecutionTargets, handle, token])!;

    private static AgentRpcService<T> Get<T>(string type, string property = "Service") where T : class
        => (AgentRpcService<T>)typeof(AgentRpcCatalog).Assembly
            .GetType("Sunder.Package.Agent.Runtime.Rpc." + type, throwOnError: true)!
            .GetProperty(property, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
}
