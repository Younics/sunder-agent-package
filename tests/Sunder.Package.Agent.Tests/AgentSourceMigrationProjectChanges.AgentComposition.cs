using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeAgentComposition(string path, string text)
    {
        text = BeforeAgentAppComposition(path, text);
        if (!path.Replace('\\', '/').Contains("/src/Sunder.Package.Agent/", StringComparison.Ordinal)) return text;
        text = BeforeCanonicalBehaviorFixes(path, text);
        const string preferred = "    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]\n";
        const string shared = "[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]\n";
        var name = Path.GetFileName(path);
        var sharedCount = name == "AgentWorkspaceRecord.cs" ? 3 : name == "AgentModelDescriptor.cs" ? 5 : name == "AgentRpcCatalog.cs" ? 10
            : name is "AgentToolDescriptor.cs" or "AgentTranscriptToolDetailRecord.cs" or "AgentTurnMutation.cs" or "AgentToolPresentation.cs"
                or "AgentExecutionShellDescriptor.cs" or "AgentResourceClaim.cs" ? 2
            : name is "AgentExecutionScopeDescriptor.cs" or "AgentExecutionTargetDescriptor.cs" or "AgentProfileSelectableCapabilityAssignmentRecord.cs" or "AgentEmbeddingModelDescriptor.cs"
                or "AgentProfileSelectableCapabilityDescriptor.cs" or "AgentToolActivationRequirement.cs" or "AgentToolPriority.cs" or "AgentToolSelectionScope.cs"
                or "AgentMemoryProvenance.cs" or "AgentSessionRecord.cs" or "AgentSessionState.cs" or "AgentSessionContextCheckpointRecord.cs" or "AgentWorkingSummaryRecord.cs"
                or "AgentProfileRecord.cs" or "AgentProfileModelBindingRecord.cs" or "AgentRunCheckpointRecord.cs" or "AgentRunStatus.cs" or "AgentRunActivityKind.cs"
                or "AgentTurnRecord.cs" or "AgentTurnItemRecord.cs" or "AgentTurnItemKind.cs" or "AgentTurnKind.cs" or "AgentMessageRole.cs" or "AgentToolExecutionStatus.cs"
                or "AgentProviderReadiness.cs" or "AgentProviderReadinessStatus.cs" or "AgentToolSourceItem.cs" or "AgentProfileSelectableCapabilityKinds.cs"
                or "AgentExecutionPathMapping.cs" or "AgentExecutionTargetContext.cs" or "AgentProcessCommandRequest.cs" or "AgentShellCommandRequest.cs"
                or "AgentWorkspaceBindingRecord.cs" ? 1 : 0;
        if (sharedCount != 0)
        {
            Assert.Equal(sharedCount + 1, text.Split(shared, StringSplitOptions.None).Length);
            text = text.Replace(shared, "", StringComparison.Ordinal);
        }
        var additions = name switch
        {
            "AgentRpcCatalog.cs" => preferred,
            "AgentProfileService.cs" => preferred
                + "    internal AgentProfileService(AgentLocalStore store, AgentToolService tools, AgentRpcCatalog catalog, Runtime.Rpc.AgentBehaviorServices behaviors)\n"
                + "        : this(store, tools, catalog, behaviors.Service, refreshProviderSubscriptions: false) { }\n\n",
            "AgentProfileStackContributor.cs" => preferred
                + "    internal AgentProfileStackContributor(AgentProfileService profiles, IPackageContext context, AgentRpcCatalog catalog, Runtime.Rpc.AgentBehaviorServices behaviors)\n"
                + "        : this(profiles, context, catalog, behaviors.Service) { }\n\n",
            "AgentBehaviorLoopResolver.cs" => preferred
                + "    internal AgentBehaviorLoopResolver(AgentRpcCatalog catalog, Runtime.Rpc.AgentBehaviorServices behaviors, DefaultAgentBehaviorLoop defaultLoop)\n"
                + "        : this(catalog, behaviors.Service, defaultLoop) { }\n\n",
            "AgentLifecycleDispatcher.cs" => preferred
                + "    internal AgentLifecycleDispatcher(AgentLocalStore store, AgentRpcCatalog catalog, IPackageContext context)\n"
                + "        : this(store, catalog, context.Logging.Events) { }\n\n",
            "DefaultAgentBehaviorLoop.cs" => preferred
                + "    internal DefaultAgentBehaviorLoop(AgentPromptPreparationPipeline prompt, AgentProviderCycleRunner provider, AgentToolCycleCoordinator tools, AgentLoopTerminalHandler terminal)\n"
                + "        : this(prompt, provider, tools, terminal, budgetLimits: null) { }\n\n",
            "AgentSessionContextProjectionService.cs" or "AgentUserMessageRunCoordinator.cs" => preferred,
            _ => "",
        };
        if (additions.Length != 0) Replace(additions, "");
        if (name == "AgentRpcCatalog.cs")
            Replace("internal AgentRpcCatalog(ISunderRpcClient client, bool startWatching = false)", "private AgentRpcCatalog(ISunderRpcClient client, bool startWatching)");
        if (name == "AgentWorkerLifecycle.cs")
        {
            Replace("    private readonly IServiceProvider? _services;\n    private readonly Func<AgentRpcCatalog> _catalog;\n    private readonly Func<AgentRuntimeStartupService> _createStartup;",
                "    private readonly IServiceProvider _services;");
            Replace("    [ActivatorUtilitiesConstructor]\n    internal AgentWorkerLifecycle(AgentRpcCatalog catalog, Lazy<AgentRuntimeStartupService> startup)\n"
                + "    {\n        _catalog = () => catalog;\n        _createStartup = () => startup.Value;\n        _providers = [];\n        _cleanupTimeout = DefaultCleanupTimeout;\n    }\n\n", "");
            Replace("        _catalog = services.GetRequiredService<AgentRpcCatalog>;\n        _createStartup = services.GetRequiredService<AgentRuntimeStartupService>;\n", "");
            Replace("var catalog = _catalog();", "var catalog = _services.GetRequiredService<AgentRpcCatalog>();");
            Replace("var startup = _createStartup();", "var startup = _services.GetRequiredService<AgentRuntimeStartupService>();");
            Replace("provider.Activate(_services!);", "provider.Activate(_services);");
            Replace("_catalog().Dispose();", "_services.GetRequiredService<AgentRpcCatalog>().Dispose();");
        }
        return text;

        void Replace(string current, string previous)
        {
            Assert.Equal(2, text.Split(current, StringSplitOptions.None).Length);
            text = text.Replace(current, previous, StringComparison.Ordinal);
        }
    }
}
