using System.Reflection;
using System.Runtime.Loader;
using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    internal static readonly string[] SourcePaths =
    [
        "src/Sunder.Package.Agent/Rpc/Public/AgentRpcValues.cs",
        "src/Sunder.Package.Agent/Rpc/Public/WorkspaceModels.cs",
        "src/Sunder.Package.Agent/Rpc/Public/WorkspaceExecutionResolver.cs",
        "src/Sunder.Package.Agent/Rpc/Public/ExecutionTarget.cs",
        "src/Sunder.Package.Agent/Rpc/Public/ExecutionContextModels.cs",
        "src/Sunder.Package.Agent/Rpc/Public/ExecutionOperationModels.cs",
        "src/Sunder.Package.Agent/Rpc/Public/ExecutionResourceModels.cs",
        "src/Sunder.Package.Agent/Rpc/Public/WorkspaceEditor.cs",
        "src/Sunder.Package.Agent/Rpc/Public/PermissionSurface.cs",
        "src/Sunder.Package.Agent/Rpc/Public/ProfileCapabilityConsumer.cs",
        "src/Sunder.Package.Agent/Rpc/Public/SelectableCapabilityProvider.cs",
        "src/Sunder.Package.Agent/Rpc/Public/EmbeddingProvider.cs",
        "src/Sunder.Package.Agent/Rpc/Public/ToolSource.cs",
        "src/Sunder.Package.Agent/Rpc/Public/ToolModels.cs",
        "src/Sunder.Package.Agent/Rpc/Public/ToolExecutionObserved.cs",
        "src/Sunder.Package.Agent/Rpc/Public/ConversationModels.cs",
        "src/Sunder.Package.Agent/Rpc/Public/DurableLifecycleObserver.cs",
        "src/Sunder.Package.Agent/Rpc/Public/SystemPromptContributor.cs",
        "src/Sunder.Package.Agent/Rpc/Public/ChatProvider.cs",
        "src/Sunder.Package.Agent/Rpc/Public/ChatValueModels.cs",
        "src/Sunder.Package.Agent/Rpc/Public/ChatMessageModels.cs",
        "src/Sunder.Package.Agent/Rpc/Public/SessionCleaner.cs",
        "src/Sunder.Package.Agent/Rpc/Public/RuntimeCatalog.cs",
        "src/Sunder.Package.Agent/Rpc/Public/RuntimeCatalogModels.cs",
        "src/Sunder.Package.Agent/Rpc/Public/RuntimeCatalogTranscriptModels.cs",
        "src/Sunder.Package.Agent/Rpc/Public/RuntimeCatalogMapping.cs",
        "src/Sunder.Package.Agent/Rpc/Public/RunControl.cs",
        "src/Sunder.Package.Agent/Rpc/Public/RunControl.Chat.cs",
        "src/Sunder.Package.Agent/Rpc/Public/RunControlModels.cs",
        "src/Sunder.Package.Agent/Rpc/Public/RunControlChatModels.cs",
        "src/Sunder.Package.Agent/Rpc/Public/PromptContextModels.cs",
        "src/Sunder.Package.Agent/Rpc/Public/BehaviorLoopModels.cs",
        "src/Sunder.Package.Agent/Rpc/Public/RunControlMapping.cs",
        "src/Sunder.Package.Agent/Rpc/Public/RunControlMapping.Context.cs",
        "src/Sunder.Package.Agent/Rpc/Public/RunControlMapping.Tools.cs",
        "src/Sunder.Package.Agent/Rpc/Public/RunControlMapping.Chat.cs",
        "src/Sunder.Package.Agent/Rpc/Public/BehaviorLoop.cs",
        "src/Sunder.Package.Agent/Rpc/Public/ChildRunExecutor.cs",
        "src/Sunder.Package.Agent/Rpc/Public/PromptContextContributor.cs",
        "src/Sunder.Package.Agent/Rpc/Public/PromptContextRequestModels.cs",
        "src/Sunder.Package.Agent/Rpc/Public/PromptContextMapping.cs",
    ];
    internal static IEnumerable<string> CompilationPaths => SourcePaths.Concat(AgentCanonicalSourceMigrationTests.ExecutionSources)
        .Concat(AgentCanonicalSourceMigrationTests.BehaviorSources)
        .Concat(["src/Sunder.Package.Agent/Rpc/Consumers/GeneratedProviderCallLifetime.cs", "src/Sunder.Package.Agent/Rpc/Consumers/AgentConsumerValues.cs"]);

    [AgentPublicSourceMigrationFact]
    public async Task PublicOwnerSource_MatchesReviewedPublicSuccessorsAndPreservesExecutionBehavior()
    {
        var repository = BuilderSourceMigrationTests.FindRepository();
        var assembly = typeof(Services.AgentRunControlService).Assembly;
        Assert.Equal("Sunder.Generated.Package.WorkerEntryPoint", assembly.EntryPoint?.DeclaringType?.FullName);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var sourceRoot = Path.Combine(repository, "src/Sunder.Package.Agent");
        var root = Directory.CreateTempSubdirectory("sunder-agent-workspace-source-").FullName;
        var loadContext = new AssemblyLoadContext("agent-workspace-source", isCollectible: true);
        try
        {
            var baselinePath = Path.Combine(sourceRoot, "sunder-contracts.owned.json");
            var baselineBytes = File.ReadAllBytes(baselinePath);
            var owned = JsonNode.Parse(baselineBytes)!["contracts"]!.AsObject()
                .Where(entry => entry.Value!["current"]!["exposure"]!.GetValue<string>() == "public").ToArray();
            Assert.Equal(18, owned.Length);
            var descriptors = new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal);
            foreach (var entry in owned)
            {
                var current = entry.Value!["current"]!;
                Assert.Equal("release-frozen", current["state"]!.GetValue<string>());
                var descriptor = SunderRpcContractDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes(current["canonicalDescriptor"]!.GetValue<string>()));
                var reviewed = CurrentPublicRpcDescriptors.Get(descriptor.ContractId);
                Assert.Equal("public", current["exposure"]!.GetValue<string>());
                AssertFrozenBytes(reviewed.GetCanonicalUtf8(), descriptor.GetCanonicalUtf8(), descriptor.ContractId + " reviewed descriptor");
                AssertFrozenBytes(reviewed.GetCanonicalAbiProjectionUtf8(), descriptor.GetCanonicalAbiProjectionUtf8(), descriptor.ContractId + " reviewed ABI");
                descriptors.Add(descriptor.ContractId, descriptor);
            }
            using (var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sourceRoot, "obj", configuration, "net10.0/sunder-analysis",
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "sunder-rpc/authoring-receipt.v1.json"))))
            {
                Assert.Equal(21, receipt.RootElement.GetProperty("bundles").GetArrayLength());
                Assert.Equal(12, receipt.RootElement.GetProperty("providers").GetArrayLength());
                var contractIds = receipt.RootElement.GetProperty("contracts").EnumerateArray().ToDictionary(
                    contract => contract.GetProperty("contractKey").GetString()!, contract => contract.GetProperty("contractId").GetString()!, StringComparer.Ordinal);
                var declaredProviders = receipt.RootElement.GetProperty("providers").EnumerateArray()
                    .Where(provider => descriptors.ContainsKey(contractIds[provider.GetProperty("contractKey").GetString()!])).ToArray();
                Assert.Equal(["agent.attachment.cleaner", "agent.behavior.loop", "agent.child.runs", "agent.history.cleaner", "agent.run.control", "agent.runtime.catalog", "agent.workspace.documentation", "agent.workspace.execution.resolver"],
                    declaredProviders.Select(provider => provider.GetProperty("providerId").GetString()!).Order(StringComparer.Ordinal).ToArray());
                Assert.All(declaredProviders, provider =>
                {
                    Assert.Equal("runtime", provider.GetProperty("role").GetString());
                    Assert.Equal("public", provider.GetProperty("audience").GetString());
                });
                AgentRuntimeUsePolicyAssertions.AssertWorkerReceipt(repository, receipt.RootElement);
            }

            Assert.Null(assembly.GetType("Sunder.Package.Agent.Rpc.Public.WorkspacePathMigratorService"));
            var execution = assembly.GetType("Sunder.Package.Agent.Rpc.Public.ExecutionTargetService", true)!;
            Assert.True(execution.IsInterface);
            Assert.Equal(19, execution.GetMethods().Length);
            foreach (var (contract, implementation, count) in new[]
                     {
                         ("RuntimeCatalog", "AgentRuntimeCatalog", 22), ("RunControl", "AgentRunControlService", 20),
                         ("WorkspaceExecutionResolver", "AgentWorkspaceExecutionResolver", 2), ("ChildRunExecutor", "AgentChildRunExecutor", 1),
                     })
            {
                Assert.Null(assembly.GetType("Sunder.Package.Agent.Rpc.Public." + contract + "Service"));
                var implementationType = assembly.GetType("Sunder.Package.Agent.Services." + implementation, true)!;
                Assert.True(implementationType.IsClass);
                Assert.Equal(SunderServiceExposure.Public, implementationType.GetCustomAttribute<SunderServiceAttribute>()!.Exposure);
                var generated = assembly.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Services.I" + contract + "Service", true)!;
                Assert.True(generated.IsInterface);
                Assert.Equal(count, generated.GetMethods().Length);
            }
            foreach (var (name, count) in new[] { ("WorkspaceEditor", 4), ("PermissionSurface", 1), ("ProfileCapabilityConsumer", 1), ("SelectableCapabilityProvider", 3), ("EmbeddingProvider", 6), ("ToolSource", 7), ("ToolExecutionObserved", 2), ("DurableLifecycleObserver", 2), ("SystemPromptContributor", 2), ("ChatProvider", 6) })
            {
                var extension = assembly.GetType("Sunder.Package.Agent.Rpc.Public." + name + "Service", true)!;
                Assert.True(extension.IsInterface);
                Assert.Equal(count, extension.GetMethods().Length);
            }
            await VerifyPromptContextAsync(assembly, descriptors);
            await VerifyChildRunsAsync(assembly, descriptors[RegressionTestBindings.ChildRunExecutor]);
            await VerifyBehaviorLoopAsync(assembly, descriptors);
            await VerifyRunControlAsync(assembly, descriptors["younics.ai.agent.run.control"]);
            await VerifyRuntimeCatalogAsync(assembly, descriptors["younics.ai.agent.runtime.catalog"]);
            await VerifyExecutionTargetAsync(root, loadContext, assembly, descriptors[RegressionTestBindings.ExecutionTargets]);
            await VerifyExtensionsAsync(root, loadContext, assembly, descriptors);
            await VerifyToolLifecycleAsync(root, loadContext, assembly, descriptors);
            await VerifyChatAsync(root, loadContext, assembly, descriptors["younics.ai.agent.chat.provider"]);
            await VerifySessionCleanersAsync(assembly, descriptors["younics.ai.agent.session.cleaner"]);
            var serviceType = assembly.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Services.IWorkspaceExecutionResolverService", true)!;
            Assert.True(serviceType.IsInterface);
            using var state = new RegressionTestRpcState();
            var client = new RegressionTestRpcClient(state, "younics.sunder.package.maker");
            await using var receiver = new GeneratedServiceTestScope(client);
            var target = new PromptTargetProbe();
            using var sources = new RegressionTestSourceProviders();
            var targetRegistration = sources.Create(RegressionTestBindings.ExecutionTargets, target, receiver);
            var targetSnapshot = state.Add("younics.ai.agent", descriptors[RegressionTestBindings.ExecutionTargets], target, targetRegistration.Handler,
                EmptyDisposable.Instance, targetRegistration.ProviderId, "2.0.0");
            var targetReference = Assert.Single((await receiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers).Reference;
            var resolver = CreateResolver(targetReference);
            var service = resolver;
            var registration = sources.Create(RegressionTestBindings.WorkspaceExecutionResolver, service, receiver);
            var resolverDescriptor = descriptors[RegressionTestBindings.WorkspaceExecutionResolver];
            state.Add("younics.ai.agent", resolverDescriptor, service, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
            var providers = (await client.DiscoverAsync(resolverDescriptor.ContractId)).Providers;
            var source = providers.Single(value => value.ProviderId == registration.ProviderId);
            var resolverJson = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
            };
            foreach (var method in new[] { "list-workspaces", "resolve" })
            {
                var request = method == "resolve" ? JsonSerializer.SerializeToElement("workspace.one") : JsonSerializer.SerializeToElement(new { });
                var resolution = resolver.Resolution;
                var expected = method == "list-workspaces" ? JsonSerializer.SerializeToElement(new[] { resolver.Workspace }, resolverJson) : JsonSerializer.SerializeToElement(new
                {
                    resolution.Binding, resolution.Scope, resolution.Target, resolution.Workspace,
                    ExecutionTarget = new
                    {
                        targetSnapshot.PackageId, targetSnapshot.PackageVersion, targetSnapshot.ProviderId,
                        targetSnapshot.ContractId, targetSnapshot.ContractVersion, targetSnapshot.DescriptorSha256,
                        targetSnapshot.AbiSha256, targetSnapshot.ActivationId, targetSnapshot.ActivationEpoch,
                        targetSnapshot.SessionGeneration, Endpoint = targetSnapshot.Endpoint.Value,
                    },
                }, resolverJson);
                var actual = await client.InvokeAsync(source.Endpoint, "workspace-execution-resolver", method, request);
                Assert.True(JsonElement.DeepEquals(expected, actual), $"{method}: {expected} != {actual}");
            }
            resolver.Resolution = resolver.Resolution with { ExecutionTarget = null! };
            var rejectedResolution = await Assert.ThrowsAsync<JsonException>(async () => await client.InvokeAsync(source.Endpoint,
                "workspace-execution-resolver", "resolve", JsonSerializer.SerializeToElement("workspace.one")));
            Assert.Same(rejectedResolution, state.LastInvocationFailure);

            using var cancellation = new CancellationTokenSource();
            resolver.BlockList = true;
            var pending = client.InvokeAsync(source.Endpoint, "workspace-execution-resolver", "list-workspaces",
                JsonSerializer.SerializeToElement(new { }), cancellationToken: cancellation.Token).AsTask();
            await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(baselineBytes, File.ReadAllBytes(baselinePath));
        }
        finally { loadContext.Unload(); Directory.Delete(root, recursive: true); }
    }

    internal static void AssertFrozenBytes(byte[] expected, byte[] actual, string label)
    {
        if (expected.AsSpan().SequenceEqual(actual)) return;
        var position = 0;
        while (position < Math.Min(expected.Length, actual.Length) && expected[position] == actual[position]) position++;
        var start = Math.Max(0, position - 100);
        Assert.Fail($"{label} differs at byte {position}; expected {expected.Length} bytes, actual {actual.Length}.\n"
            + "Expected: " + System.Text.Encoding.UTF8.GetString(expected, start, Math.Min(240, expected.Length - start)) + "\n"
            + "Actual:   " + System.Text.Encoding.UTF8.GetString(actual, start, Math.Min(240, actual.Length - start)));
    }

    private static Resolver CreateResolver(ServiceReference<AgentServices.IExecutionTargetService> executionTarget)
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 34, 56, TimeSpan.FromHours(2)).AddTicks(1234567);
        var workspace = new AgentWorkspaceRecord("workspace.one", "Workspace 漢字", null, now, now,
            [new("path.one", "workspace.one", "/work/one", true, -1, now, now)],
            [new("document.one", "workspace.one", "README.md", 2, now, now)]);
        var binding = new AgentWorkspaceBindingRecord("binding.one", workspace.WorkspaceId, RegressionTestBindings.ExecutionTargets,
            "local", "primary", true, 0, now, now);
        var target = new AgentExecutionTargetDescriptor("local", "local", "Local", null, true, true) { Facets = ["execution-scope"] };
        return new Resolver
        {
            Workspace = workspace,
            Resolution = new(workspace, binding, target, new("Local", ["/work/one"], "/work/one"), executionTarget),
        };
    }

    private sealed class Resolver : IAgentWorkspaceExecutionResolver
    {
        public required AgentWorkspaceRecord Workspace { get; init; }
        public required AgentWorkspaceExecutionResolution Resolution { get; set; }
        public bool BlockList { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(CancellationToken cancellationToken = default)
        {
            if (BlockList) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return [Workspace];
        }
        public ValueTask<AgentWorkspaceExecutionResolution> ResolveAsync(string workspaceId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Workspace.WorkspaceId, workspaceId);
            return ValueTask.FromResult(Resolution);
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        internal static EmptyDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}

internal sealed class AgentPublicSourceMigrationFactAttribute : FactAttribute
{
    public AgentPublicSourceMigrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION") == "1") return;
        var root = BuilderSourceMigrationTests.FindRepository();
        if (!File.Exists(Path.GetFullPath(Path.Combine(root, "..", "sunder-core", "Sunder.Core.slnx"))))
            Skip = "Requires sibling Core source/tooling. Set SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION=1 to require this lane.";
    }
}
