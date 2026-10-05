using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    private static string BeforeSubagentCatalogBindings(string path, string text)
    {
        if (Path.GetFileName(path) == "SubagentRuntimeBridge.cs")
        {
            var legacy = ReadMigrationSourceText(Path.Combine(Path.GetDirectoryName(path)!, "SubagentLocalManagementGateway.Legacy.cs"));
            const string app = "#if SUNDER_SUBAGENTS_APP";
            Assert.Equal(2, text.Split(app, StringSplitOptions.None).Length);
            text = text.Replace(app, legacy[legacy.IndexOf("#if SUNDER_SUBAGENTS_WORKER", StringComparison.Ordinal)..].TrimEnd() + "\n\n" + app, StringComparison.Ordinal);
        }
        const string restoreUsing = "using Sunder.Package.Agent.Contracts.Models;\n";
        var replacements = Path.GetFileName(path) switch
        {
            "SubagentFeature.cs" => new (string After, string Before, int Count)[]
            {
                (restoreUsing, restoreUsing + "using Sunder.Package.Agent.Protocol;\n", 1),
                ("        ISubagentRuntimeBindings runtimeBindings)", "        AgentRpcCatalog rpcCatalog,\n        ISubagentRuntimeBindings runtimeBindings)", 1),
                ("new SubagentPermissionStatusAdapter(runtimeBindings)", "new SubagentPermissionStatusAdapter(rpcCatalog)", 1),
                ("new SubagentDescriptorSchema(subagentService, permissionStatusAdapter, runtimeBindings)", "new SubagentDescriptorSchema(subagentService, rpcCatalog, permissionStatusAdapter, runtimeBindings)", 1),
            },
            "SubagentDescriptorSchema.cs" =>
            [
                (restoreUsing, restoreUsing + "using Sunder.Package.Agent.Protocol;\n", 1),
                ("    SubagentService subagentService,\n", "    SubagentService subagentService,\n    AgentRpcCatalog rpcCatalog,\n", 1),
                ("    public ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(",
                    "    private readonly AgentRpcProviderService<IAgentBehaviorLoop> _behaviorLoops =\n        AgentRpcServices.CreateBehaviorLoops(new AgentRunControlRegistry(), rpcCatalog);\n\n"
                    + "    public ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(", 1),
                ("runtimeBindings.GetBehaviorLoops()", "rpcCatalog.GetServiceReferences(_behaviorLoops)", 1),
            ],
            "SubagentRuntimeBridge.cs" =>
            [
                (restoreUsing, restoreUsing + "using Sunder.Package.Agent.Protocol;\n", 1),
                ("    ISubagentRuntimeBindings runtimeBindings) : IDisposable", "    AgentRpcCatalog rpcCatalog,\n    ISubagentRuntimeBindings runtimeBindings) : IDisposable", 1),
                ("new(runtimeBindings)", "new(rpcCatalog)", 1),
                ("SubagentCatalogRpcReference<ISubagentChatCatalog,", "SubagentCatalogRpcReference<IAgentChatProvider,", 1),
                ("            runtimeBindings.GetChatProviders,", "            rpcCatalog,\n            AgentRpcServices.ChatProviders,", 1),
            ],
            "SubagentRuntimeChangeStream.cs" =>
            [
                (restoreUsing, restoreUsing + "using Sunder.Package.Agent.Protocol;\n", 1),
                ("        ISubagentRuntimeBindings runtimeBindings)", "        AgentRpcCatalog rpcCatalog,\n        ISubagentRuntimeBindings runtimeBindings)", 1),
                ("new SubagentEditorCapabilityCatalog(runtimeBindings)", "new SubagentEditorCapabilityCatalog(rpcCatalog)", 1),
                ("Catalog availability is reported by the bindings;", "Catalog availability is reported by AgentRpcCatalog;", 1),
            ],
            "SubagentPermissionStatusAdapter.cs" =>
            [
                ("using Sunder.Package.Agent.Subagents.Runtime;", "using Sunder.Package.Agent.Protocol;", 1),
                ("internal sealed partial class SubagentPermissionStatusAdapter(ISubagentRuntimeBindings runtimeBindings)", "internal sealed class SubagentPermissionStatusAdapter(AgentRpcCatalog rpcCatalog)", 1),
                ("runtimeBindings.GetToolSources()", "rpcCatalog.GetServiceReferences(AgentRpcServices.ToolSources)", 1),
            ],
            "SubagentStackContributor.cs" =>
            [
                ("using Sunder.Package.Agent.Subagents.Runtime;", "using Sunder.Package.Agent.Protocol;", 1),
                ("internal sealed partial class SubagentStackContributor", "internal sealed class SubagentStackContributor", 1),
                ("ISubagentRuntimeBindings runtimeBindings)", "AgentRpcCatalog rpcCatalog)", 1),
                ("runtimeBindings.GetChatProviders()", "AgentRpcServices.ChatProviders", 1),
                ("runtimeBindings.GetSelectableCapabilityProviders()", "AgentRpcServices.SelectableCapabilityProviders", 1),
                ("IReadOnlyList<SubagentProviderReference<TContract>> references,", "AgentRpcService<TContract> service,", 1),
                ("foreach (var reference in references)", "foreach (var reference in rpcCatalog.GetServiceReferences(service))", 1),
            ],
            "SubagentCatalogRpcInvocation.cs" =>
            [
                ("using Sunder.Package.Agent.Subagents.Runtime;", "using Sunder.Package.Agent.Protocol;", 1),
                ("        Func<IReadOnlyList<SubagentProviderReference<TService>>> discover,", "        AgentRpcCatalog catalog,\n        AgentRpcService<TService> service,", 1),
                ("SubagentProviderReference<TService>", "AgentRpcReference<TService>", 2),
                ("references = discover();", "references = catalog.GetServiceReferences(service);", 1),
                ("SubagentProviderLease<TService>", "AgentRpcLease<TService>", 2),
            ],
            "SubagentEditorCapabilityCatalog.cs" =>
            [
                ("using Sunder.Package.Agent.Subagents.Runtime;", "using Sunder.Package.Agent.Protocol;", 1),
                ("internal sealed partial class SubagentEditorCapabilityCatalog", "internal sealed class SubagentEditorCapabilityCatalog", 1),
                ("private readonly ISubagentRuntimeBindings _bindings;", "private readonly AgentRpcCatalog _rpcCatalog;", 1),
                ("SubagentEditorCapabilityCatalog(ISubagentRuntimeBindings bindings)", "SubagentEditorCapabilityCatalog(AgentRpcCatalog rpcCatalog)", 1),
                ("_bindings = bindings;", "_rpcCatalog = rpcCatalog;", 1),
                ("_bindings.CatalogChanged", "_rpcCatalog.Changed", 2),
                ("            _bindings.GetToolSources,", "            _rpcCatalog,\n            AgentRpcServices.ToolSources,", 1),
                ("            _bindings.GetSelectableCapabilityProviders,", "            _rpcCatalog,\n            AgentRpcServices.SelectableCapabilityProviders,", 1),
                ("OnCatalogChanged(SubagentCatalogChange change)", "OnCatalogChanged(object? sender, AgentRpcCatalogChangedEventArgs e)", 1),
                ("        if ((change & (SubagentCatalogChange.ChatProviders | SubagentCatalogChange.Tools\n            | SubagentCatalogChange.SelectableCapabilities | SubagentCatalogChange.RuntimeCatalog)) == 0)",
                    "        if (!e.IncludesContract(AgentRpcContractIds.ChatProvider)\n            && !e.IncludesContract(AgentRpcContractIds.ToolSource)\n            && !e.IncludesContract(AgentRpcContractIds.SelectableCapabilityProvider)\n            && !e.IncludesContract(AgentRpcContractIds.RuntimeCatalog))", 1),
                ("if ((change & SubagentCatalogChange.SelectableCapabilities) != 0)", "if (e.IncludesContract(AgentRpcContractIds.SelectableCapabilityProvider))", 1),
                ("_bindings.GetSelectableCapabilityProviders()", "_rpcCatalog.GetServiceReferences(\n                         AgentRpcServices.SelectableCapabilityProviders)", 1),
                ("                added.ObserveRetirement(() => OnProviderRetired(added));\n", "", 1),
                ("\n    private void OnProviderRetired(ProviderSubscription subscription)\n    {\n        lock (_subscriptionGate)\n        {\n            if (!_providerSubscriptions.Remove(subscription)) return;\n            subscription.Notifier.SelectableCapabilitiesChanged -= OnSelectableCapabilitiesChanged;\n            subscription.Dispose();\n        }\n    }\n", "", 1),
                ("SubagentProviderLease<IAgentProfileSelectableCapabilityProvider>", "AgentRpcLease<IAgentProfileSelectableCapabilityProvider>", 2),
                ("        private readonly CancellationToken _retirementToken = lease.RetirementToken;\n        private CancellationTokenRegistration _retirement;\n", "", 1),
                ("        internal void ObserveRetirement(Action retired)\n        {\n            _retirement = _retirementToken.Register(retired);\n            if (_lease is null) _retirement.Unregister();\n        }\n        public void Dispose()\n        {\n            _retirement.Unregister();\n            Interlocked.Exchange(ref _lease, null)?.Dispose();\n        }",
                    "        public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();", 1),
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
}
