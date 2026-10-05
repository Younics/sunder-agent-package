using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    // Exact inverses for the canonical behavior corrections, not exemptions from
    // source checks. Before fragments were verified against authentic index blobs:
    // SubagentRuntimeBridge: 6f832a8ac04a7041eaced09baa0e2ae190cfb3f2
    // AgentRuntimeTransportBudgetState: d7388390626334d1b8b5f1f1eef9ec2a65104a8c
    // AgentLifecycleDispatcher: 2471be6ba43268f891eb4fc50de8f6eb04ffa615
    // The complete inverse chain must still produce each immutable baseline hash.
    internal static string BeforeCanonicalBehaviorFixes(string path, string text)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.EndsWith("/src/Sunder.Package.Agent/Runtime/AgentRuntimeTransportBudgetState.cs", StringComparison.Ordinal))
        {
            Replace("    internal AgentRuntimeTransportBudgetState() : this(null)\n    {\n    }\n\n"
                + "    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]\n"
                + "    internal AgentRuntimeTransportBudgetState([System.Diagnostics.CodeAnalysis.AllowNull] ISunderRpcClient client)",
                "    internal AgentRuntimeTransportBudgetState(ISunderRpcClient? client = null)");
        }
        else if (normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentLifecycleDispatcher.cs", StringComparison.Ordinal))
        {
            Replace("    internal async Task FlushAsync(CancellationToken cancellationToken = default)\n    {\n        cancellationToken.ThrowIfCancellationRequested();\n",
                "    internal async Task FlushAsync(CancellationToken cancellationToken = default)\n    {\n");
            Replace("var replayPending = ReconcileSubscriptions(cancellationToken);", "var replayPending = ReconcileSubscriptions();");
            Replace("    private bool ReconcileSubscriptions(CancellationToken cancellationToken)", "    private bool ReconcileSubscriptions()");
            Replace("""
                        var observers = AgentRpcInvocation.Snapshot(
                            _rpcCatalog,
                            Sunder.Package.Agent.Runtime.Rpc.AgentCleanupBindings.DurableLifecycleObservers,
                            static observer => (observer.ObserverId, observer.DisplayName),
                            cancellationToken,
                            omitUnavailable: true);
                        foreach (var observer in observers)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            replayPending |= AddSubscription(
                                active,
                                observer.PackageId,
                                observer.Metadata.ObserverId,
                                observer.Metadata.DisplayName,
                                "Durable",
                                subscription => new DurableLifecycleSubscription(subscription, observer.Reference),
                                now);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                """, """
                        foreach (var reference in _rpcCatalog.GetServiceReferences(Sunder.Package.Agent.Runtime.Rpc.AgentCleanupBindings.DurableLifecycleObservers))
                        {
                            if (reference.TryAcquire(out var lease))
                            {
                                using (lease)
                                {
                                    replayPending |= AddSubscription(
                                        active,
                                        lease.PackageId,
                                        lease.Service.ObserverId,
                                        lease.Service.DisplayName,
                                        "Durable",
                                        subscription => new DurableLifecycleSubscription(subscription, reference),
                                        now);
                                }
                            }
                        }

                """);
        }
        else if (normalized.EndsWith("/src/Sunder.Package.Agent.Subagents/Runtime/SubagentRuntimeBridge.cs", StringComparison.Ordinal))
        {
            Replace("    private Task<SubagentProjection>? _runtimeSnapshot;",
                "    private IReadOnlyList<ProviderCatalogOption>? _providers;\n    private Task<SubagentProjection>? _runtimeSnapshot;");
            Replace("        return projection.Subagents ?? [];", "        UpdateProviders(projection);\n        return projection.Subagents ?? [];");
            Replace("""
                    public async Task<IReadOnlyList<ProviderCatalogOption>> ListChatProvidersAsync(
                        CancellationToken cancellationToken = default)
                    {
                        // Explicit editor refreshes must query the current catalog even when its
                        // asynchronous change notification has not reached the App yet.
                        var projection = await InvokeAsync(new(SubagentQueryKind.Management), cancellationToken).ConfigureAwait(false);
                        return (projection.Providers ?? []).Select(provider =>
                            new ProviderCatalogOption(provider.ProviderId, provider.DisplayName, provider.PackageId)).ToArray();
                    }
                """, """
                    public Task<IReadOnlyList<ProviderCatalogOption>> ListChatProvidersAsync(
                        CancellationToken cancellationToken = default)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return Task.FromResult(_providers
                            ?? throw new InvalidOperationException(
                                "Subagent management must initialize before providers are read."));
                    }
                """);
            Replace("                _ = await ListChatProvidersAsync(cancellationToken).ConfigureAwait(false);",
                "                UpdateProviders(await InvokeAsync(\n                    new(SubagentQueryKind.Management),\n                    cancellationToken).ConfigureAwait(false));");
            Replace("\n}\n#endif\n\n#if SUNDER_SUBAGENTS_WORKER",
                "\n    private void UpdateProviders(SubagentProjection projection)\n"
                + "        => _providers = (projection.Providers ?? []).Select(provider =>\n"
                + "            new ProviderCatalogOption(provider.ProviderId, provider.DisplayName, provider.PackageId)).ToArray();\n"
                + "}\n#endif\n\n#if SUNDER_SUBAGENTS_WORKER");
        }
        return text;

        void Replace(string current, string original)
        {
            Assert.Equal(2, text.Split(current, StringSplitOptions.None).Length);
            text = text.Replace(current, original, StringComparison.Ordinal);
        }
    }
}
