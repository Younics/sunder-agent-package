using System.Runtime.Loader;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    // Preparation is an explicit parent-owned step. These source scenarios share
    // the validated module; they never Pack, restore, select or generate at runtime.
    // root/loadContext remain in the signature for the existing scenario lifetimes.
    internal static Task<object> CreateGeneratedExtensionProviderAsync(string root, AssemblyLoadContext loadContext,
        string service, Func<string, Type, object?, CancellationToken, object> invoke)
    {
        var family = service switch
        {
            "WorkspaceEditor" => "WorkspaceEditor",
            "PermissionSurface" => "Permission",
            "ProfileCapabilityConsumer" => "ProfileCapability",
            "SelectableCapabilityProvider" => "SelectableCapability",
            "EmbeddingProvider" => "Embedding",
            "WorkspaceExecutionResolver" => "WorkspaceResolver",
            "RuntimeCatalog" => "RuntimeCatalog",
            "ChatProvider" => "Chat",
            "ToolSource" => "ToolSource",
            "ToolExecutionObserved" => "ObservedTool",
            "DurableLifecycleObserver" => "DurableLifecycle",
            "SystemPromptContributor" => "SystemPrompt",
            _ => throw new InvalidOperationException("No prepared extension provider is registered for " + service),
        };
        var provider = RegressionTestGeneratedProviders.CreatePreparedProvider(family,
            (method, result, arguments, token) => invoke(method, result, arguments.Length switch
            {
                0 => null,
                1 => arguments[0],
                _ => throw new InvalidOperationException("The generated extension API must supply one request value."),
            }, token));
        return Task.FromResult(provider);
    }
}
