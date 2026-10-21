using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentCanonicalSourceMigrationTests
{
    internal const string LegacyCatalog = "src/Sunder.Package.Agent/Runtime/AgentWorkerProviderCatalog.Legacy.cs";
    internal const string OwnerProviderPort = "src/Sunder.Package.Agent/Runtime/IAgentOwnerRuntimeProvider.cs";
    internal const string LegacyEmbeddingBindings = "src/Sunder.Package.Agent/Runtime/Rpc/AgentEmbeddingBindings.Legacy.cs";
    internal const string LegacyChatBindings = "src/Sunder.Package.Agent/Runtime/Rpc/AgentChatBindings.Legacy.cs";
    internal const string LegacyBehaviorBindings = "src/Sunder.Package.Agent/Runtime/Rpc/AgentBehaviorBindings.Legacy.cs";
    internal const string LegacyCleanupBindings = "src/Sunder.Package.Agent/Runtime/Rpc/AgentCleanupBindings.Legacy.cs";
    internal const string LegacyMetadataBindings = "src/Sunder.Package.Agent/Runtime/Rpc/AgentMetadataBindings.Legacy.cs";
    internal const string LegacySelectableBindings = "src/Sunder.Package.Agent/Runtime/Rpc/AgentSelectableBindings.Legacy.cs";
    internal const string LegacyToolProfileBindings = "src/Sunder.Package.Agent/Runtime/Rpc/AgentToolProfileBindings.Legacy.cs";
    internal const string LegacyPromptContextBindings = "src/Sunder.Package.Agent/Runtime/Rpc/AgentPromptContextBindings.Legacy.cs";
    internal const string LegacyExecutionBindings = "src/Sunder.Package.Agent/Runtime/Rpc/AgentExecutionBindings.Legacy.cs";
    internal const string LegacyToolBindings = "src/Sunder.Package.Agent/Runtime/Rpc/AgentToolBindings.Legacy.cs";
    internal const string LegacyObservedToolBindings = "src/Sunder.Package.Agent/Runtime/Rpc/AgentObservedToolBindings.Legacy.cs";
    internal static readonly string[] EmbeddingSources = Consumers("GeneratedProviderCallLifetime.cs");
    internal static readonly string[] CleanupSources = Consumers("AgentCleanupBindings.cs", "GeneratedSessionCleanerClient.cs", "GeneratedDurableLifecycleClient.cs");
    internal static readonly string[] ChatSources = Consumers("AgentChatBindings.cs", "GeneratedChatProviderClient.cs");
    internal static readonly string[] SourcePaths =
    [
        LegacyCatalog, "src/Sunder.Package.Agent/Runtime/AgentWorkerComposition.cs",
        "src/Sunder.Package.Agent/Runtime/Rpc/AgentBehaviorServices.cs", "src/Sunder.Package.Agent/Rpc/Owner/ProviderComposition.cs",
    ];
    internal static readonly string[] BehaviorSources = Consumers("AgentBehaviorBindings.cs", "GeneratedBehaviorLoopClient.cs",
        "GeneratedRunControlEndpoint.cs", "GeneratedBehaviorRuntime.cs", "GeneratedBehaviorRuntime.Chat.cs");
    internal static readonly string[] MetadataSources = Consumers("AgentMetadataBindings.cs", "AgentConsumerValues.cs",
        "GeneratedProfileCapabilityClient.cs", "GeneratedSystemPromptClient.cs", "GeneratedPermissionSurfaceClient.cs");
    internal static readonly string[] SelectableSources = Consumers("AgentSelectableBindings.cs", "GeneratedSelectableCapabilityClient.cs");
    internal static readonly string[] PromptContextSources = Consumers("AgentPromptContextBindings.cs", "GeneratedPromptContextClient.cs");
    internal static readonly string[] ExecutionSources = Consumers("AgentExecutionBindings.cs", "AgentExecutionValues.cs",
        "GeneratedExecutionTargetClient.cs", "GeneratedExecutionTargetClient.Facets.cs");
    internal static readonly string[] ToolSources = Consumers("AgentToolBindings.cs", "AgentToolValues.cs", "GeneratedToolSourceClient.cs");
    internal static readonly string[] ObservedToolSources = Consumers("AgentObservedToolBindings.cs", "GeneratedObservedToolExecutionClient.cs");
    private static string[] Consumers(params string[] files) => files.Select(file => "src/Sunder.Package.Agent/Rpc/Consumers/" + file).ToArray();

    [AgentPublicSourceMigrationFact]
    public async Task CompleteWorkerSource_UsesCanonicalStartupAndGeneratedProvidersWithRealPersistence()
    {
        var repository = RegressionTestRpcDescriptors.RepositoryRoot;
        using var roles = new CanonicalAgentRoles();
        using var receipt = CanonicalAgentRoles.Receipt();
        using var plan = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CanonicalAgentRoles.AnalysisRoot, "package-role-plan.v1.json")));
        var sources = plan.RootElement.GetProperty("types").EnumerateArray()
            .Where(type => type.GetProperty("roles").GetString() is "worker" or "shared")
            .SelectMany(type => type.GetProperty("sources").EnumerateArray()).Select(path => path.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(sources.Length > 200, "The canonical Worker plan must retain the complete Agent implementation.");
        Assert.DoesNotContain("Program.cs", sources);
        Assert.DoesNotContain("Runtime/AgentWorkerProviderCatalog.Legacy.cs", sources);
        Assert.DoesNotContain(sources, source => source.EndsWith(".Legacy.cs", StringComparison.Ordinal));
        Assert.True(plan.RootElement.GetProperty("hasApp").GetBoolean());
        Assert.NotEmpty(plan.RootElement.GetProperty("workerCompile").EnumerateArray());
        Assert.NotEmpty(plan.RootElement.GetProperty("appCompile").EnumerateArray());
        Assert.Equal(21, receipt.RootElement.GetProperty("bundles").GetArrayLength());
        Assert.Equal(12, receipt.RootElement.GetProperty("providers").GetArrayLength());
        AgentRuntimeUsePolicyAssertions.AssertWorkerReceipt(repository, receipt.RootElement);
        var baselinePath = Path.Combine(CanonicalAgentRoles.SourceRoot, "sunder-contracts.owned.json");
        var baseline = File.ReadAllBytes(baselinePath);
        var descriptors = receipt.RootElement.GetProperty("contracts").EnumerateArray().ToDictionary(
            contract => contract.GetProperty("contractId").GetString()!,
            contract => SunderRpcContractDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes(contract.GetProperty("canonicalDescriptor").GetString()!)));
        foreach (var descriptor in descriptors.Values.Where(value => value.ContractId != "sunder.stack.contributor"))
        {
            if (descriptor.ContractId is "younics.ai.agent.owner.runtime" or "younics.ai.agent.agent.profile")
            {
                CurrentPrivateRpcDescriptors.AssertCurrent(descriptor);
                continue;
            }
            var reviewed = CurrentPublicRpcDescriptors.Get(descriptor.ContractId);
            Assert.Equal(reviewed.GetCanonicalUtf8(), descriptor.GetCanonicalUtf8());
            Assert.Equal(reviewed.GetCanonicalAbiProjectionUtf8(), descriptor.GetCanonicalAbiProjectionUtf8());
            Assert.Equal(reviewed.Sha256, descriptor.Sha256);
            Assert.Equal(reviewed.AbiSha256, descriptor.AbiSha256);
        }
        await ExerciseComposition(roles.Worker, roles.App, descriptors);
        await AgentSourceArtifactMigrationTests.VerifyAgentChatConsumer(roles.Worker);
        await AgentSourceArtifactMigrationTests.VerifyAgentWorkspaceEditorConsumer(roles.App);
        await AgentSourceArtifactMigrationTests.VerifyAgentToolProfiles(roles.Worker);
        await AgentSourceArtifactMigrationTests.VerifyAgentPromptContext(roles.Worker);
        await AgentSourceArtifactMigrationTests.VerifyAgentExecutionConsumer(roles.Worker);
        await AgentSourceArtifactMigrationTests.VerifyAgentToolConsumers(roles.Worker, roles.App);
        await AgentOwnerSourceMigrationTests.VerifyRoleLocalBehavior(repository, CanonicalAgentRoles.SourceRoot,
            AssemblyLoadContext.GetLoadContext(roles.Worker)!, roles.Worker, roles.App, descriptors["younics.ai.agent.owner.runtime"]);
        Assert.Equal(baseline, File.ReadAllBytes(baselinePath));
    }

    internal static async Task ExerciseComposition(Assembly assembly, Assembly app, IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptors)
    {
        using var scope = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "younics.ai.agent");
        Assert.Null(assembly.GetType("AgentWorkerProviderCatalog"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Runtime.AgentWorkerComposition"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.PackageModule"));
        var (options, _, _) = AgentSourceArtifactMigrationTests.CreateGeneratedWorker(assembly, scope.Context, rpc);
        await using var composition = options.Composition!;
        Assert.Equal(12, options.Providers.Count);
        Assert.Empty(Directory.GetFiles(scope.RootPath, "*", SearchOption.AllDirectories));
        var lifecycleType = assembly.GetType("Sunder.Package.Agent.Runtime.AgentWorkerLifecycle", true)!;
        var lifecycle = composition.Services.GetService(lifecycleType)!;
        var commit = lifecycleType.GetMethod("CommitGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var activate = lifecycleType.GetMethod("ActivateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Func<string, string, CancellationToken, ValueTask>, CancellationToken, ValueTask>>(lifecycle);
        var stop = lifecycleType.GetMethod("StopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<CancellationToken, ValueTask>>(lifecycle);
        await ((ISunderWorkerLifecycleService)lifecycle).PrepareCandidateAsync();
        commit.Invoke(lifecycle, [Activator.CreateInstance(commit.GetParameters()[0].ParameterType, Guid.NewGuid(), 1L)]);
        Assert.Empty(Directory.GetFiles(scope.RootPath, "*", SearchOption.AllDirectories));
        Assert.Equal(0, state.DiscoveryCount);
        try
        {
            await activate(static (_, _, _) => ValueTask.CompletedTask, default).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            foreach (var provider in options.Providers)
                state.Add("younics.ai.agent", descriptors[provider.ContractId], provider.Handler, provider.Handler, Noop.Instance, provider.ProviderId, "2.0.0");
            var profiles = options.Providers.Single(provider => provider.ContractId == "younics.ai.agent.agent.profile");
            var profile = await profiles.Handler.InvokeUnaryAsync(null!, "agent-profile", "create-profile",
                JsonSerializer.SerializeToElement("Canonical source profile"));
            Assert.Equal("Canonical source profile", profile.GetProperty("displayName").GetString());
            var stack = options.Providers.Single(provider => provider.ProviderId == "agent.stack.profiles");
            var items = await stack.Handler.InvokeUnaryAsync(null!, "stack-contributor", "list-export-items",
                JsonSerializer.SerializeToElement(new { packageId = "younics.ai.agent" }));
            Assert.Contains(items.GetProperty("items").EnumerateArray(), item => item.GetProperty("displayName").GetString() == "Canonical source profile");
            await AgentAppSourceMigrationTests.Exercise(app, rpc, scope.Context);
        }
        finally { await stop(default); }
        await composition.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => composition.Services.GetService(lifecycleType));
    }

    internal static void AssertRoleLocalOwnerModels(Assembly assembly)
    {
        AgentSourceArtifactMigrationTests.AssertAgentWorkerSemanticOwnership(assembly);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.OwnerProtocol" or "Sunder.Package.Agent.App");
        foreach (var type in new[] { typeof(AgentServices.IAgentRuntimeService), typeof(AgentDashboardProjection), typeof(AgentTransportProfile),
            typeof(AgentSessionCommand), typeof(AgentAttachmentUploadHandle), typeof(AgentOwnerOperationException) })
        {
            var local = assembly.GetType(type.FullName!, true)!;
            Assert.Same(assembly, local.Assembly);
            Assert.NotSame(type, local);
        }
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Runtime.AgentOwnerRuntimeRpc"));
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Runtime.AgentRuntimeOperations"));
    }

    private sealed class Noop : IDisposable
    {
        internal static Noop Instance { get; } = new();
        public void Dispose() { }
    }
}
