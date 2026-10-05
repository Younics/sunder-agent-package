using System.Reflection;
using System.Runtime.Loader;
using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    internal static readonly string[] SourcePaths =
    [
        "src/Sunder.Package.Agent/Rpc/Public/AgentRpcValues.cs",
        "src/Sunder.Package.Agent/Rpc/Public/WorkspaceModels.cs",
        "src/Sunder.Package.Agent/Rpc/Public/WorkspaceExecutionResolver.cs",
        "src/Sunder.Package.Agent/Rpc/Public/WorkspacePathMigrator.cs",
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
    public async Task PublicOwnerSource_ReproducesFrozenContractsAndPreservesExecutionBehavior()
    {
        var repository = BuilderSourceMigrationTests.FindRepository();
        var assembly = typeof(Rpc.Public.RunControlService).Assembly;
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
            Assert.Equal(19, owned.Length);
            using var oracle = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repository, "compatibility/agent-rpc-migration-baseline.json")));
            var descriptors = new Dictionary<string, SunderRpcContractDescriptor>(StringComparer.Ordinal);
            foreach (var entry in owned)
            {
                var current = entry.Value!["current"]!;
                Assert.Equal("release-frozen", current["state"]!.GetValue<string>());
                var descriptor = SunderRpcContractDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes(current["canonicalDescriptor"]!.GetValue<string>()));
                var original = oracle.RootElement.GetProperty("descriptors").EnumerateArray().Single(value => value.GetProperty("contractId").GetString() == descriptor.ContractId);
                Assert.Equal(original.GetProperty("exposure").GetString(), current["exposure"]!.GetValue<string>());
                AssertFrozenBytes(Convert.FromBase64String(original.GetProperty("canonicalDescriptorBase64").GetString()!), descriptor.GetCanonicalUtf8(), descriptor.ContractId + " descriptor");
                Assert.Equal(original.GetProperty("descriptorSha256").GetString(), descriptor.Sha256);
                AssertFrozenBytes(Convert.FromBase64String(original.GetProperty("canonicalAbiProjectionBase64").GetString()!), descriptor.GetCanonicalAbiProjectionUtf8(), descriptor.ContractId + " ABI");
                Assert.Equal(original.GetProperty("abiSha256").GetString(), descriptor.AbiSha256);
                descriptors.Add(descriptor.ContractId, descriptor);
            }
            using (var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sourceRoot, "obj", configuration, "net10.0/sunder-analysis",
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "sunder-rpc/authoring-receipt.v1.json"))))
            {
                Assert.Equal(21, receipt.RootElement.GetProperty("bundles").GetArrayLength());
                Assert.Equal(11, receipt.RootElement.GetProperty("providers").GetArrayLength());
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

            var declaration = assembly.GetType("Sunder.Package.Agent.Rpc.Public.WorkspacePathMigratorService", true)!;
            Assert.True(declaration.IsInterface);
            Assert.Equal(4, declaration.GetMethods().Length);
            var execution = assembly.GetType("Sunder.Package.Agent.Rpc.Public.ExecutionTargetService", true)!;
            Assert.True(execution.IsInterface);
            Assert.Equal(19, execution.GetMethods().Length);
            foreach (var (name, count) in new[] { ("WorkspaceEditor", 4), ("PermissionSurface", 1), ("ProfileCapabilityConsumer", 1), ("SelectableCapabilityProvider", 3), ("EmbeddingProvider", 6), ("ToolSource", 7), ("ToolExecutionObserved", 2), ("DurableLifecycleObserver", 2), ("SystemPromptContributor", 2), ("ChatProvider", 6) })
            {
                var extension = assembly.GetType("Sunder.Package.Agent.Rpc.Public." + name + "Service", true)!;
                Assert.True(extension.IsInterface);
                Assert.Equal(count, extension.GetMethods().Length);
            }
            await VerifyPromptContextAsync(assembly, descriptors);
            await VerifyChildRunsAsync(assembly, descriptors[AgentRpcContractIds.ChildRunExecutor]);
            await VerifyBehaviorLoopAsync(assembly, descriptors);
            await VerifyRunControlAsync(assembly, descriptors["sunder.agent.run.control"]);
            await VerifyRuntimeCatalogAsync(assembly, descriptors["sunder.agent.runtime.catalog"]);
            await VerifyExecutionTargetAsync(root, loadContext, assembly, descriptors[AgentRpcContractIds.ExecutionTarget]);
            await VerifyExtensionsAsync(root, loadContext, assembly, descriptors);
            await VerifyToolLifecycleAsync(root, loadContext, assembly, descriptors);
            await VerifyChatAsync(root, loadContext, assembly, descriptors["sunder.agent.chat.provider"]);
            await VerifySessionCleanersAsync(assembly, descriptors["sunder.agent.session.cleaner"]);
            var serviceType = assembly.GetType("Sunder.Package.Agent.Rpc.Public.WorkspaceExecutionResolverService", true)!;
            using var state = new RegressionTestRpcState();
            var client = new RegressionTestRpcClient(state, "sunder.package.agent.builder");
            var resolver = CreateResolver(client);
            var service = Activator.CreateInstance(serviceType, resolver)!;
            var registration = Assert.IsType<SunderWorkerProviderRegistration>(serviceType.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(service, null));
            var resolverDescriptor = descriptors[AgentRpcContractIds.WorkspaceExecutionResolver];
            state.Add("sunder.package.agent", resolverDescriptor, service, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
            var providers = (await client.DiscoverAsync(resolverDescriptor.ContractId)).Providers;
            var source = providers.Single(value => value.ProviderId == registration.ProviderId);
            foreach (var method in new[] { "list-workspaces", "resolve" })
            {
                var request = method == "resolve" ? JsonSerializer.SerializeToElement(new { value = "workspace.one" }) : JsonSerializer.SerializeToElement(new { });
                var resolution = resolver.Resolution;
                var expected = method == "list-workspaces" ? RunJson(new[] { resolver.Workspace }) : RunJson(new
                {
                    resolution.Binding, ExecutionTarget = resolution.ExecutionTargetHandle, resolution.Scope, resolution.Target, resolution.Workspace,
                });
                var actual = await client.InvokeAsync(source.Endpoint, "workspace-execution-resolver", method, request);
                Assert.True(JsonElement.DeepEquals(expected, actual), $"{method}: {expected} != {actual}");
            }
            resolver.Resolution = resolver.Resolution with { ExecutionTargetHandle = null };
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.InvokeAsync(source.Endpoint,
                "workspace-execution-resolver", "resolve", JsonSerializer.SerializeToElement(new { value = "workspace.one" })));

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

    private static Resolver CreateResolver(ISunderRpcClient client)
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 34, 56, TimeSpan.FromHours(2)).AddTicks(1234567);
        var workspace = new AgentWorkspaceRecord("workspace.one", "Workspace 漢字", null, now, now,
            [new("path.one", "workspace.one", "/work/one", true, -1, now, now)],
            [new("document.one", "workspace.one", "README.md", 2, now, now)]);
        var binding = new AgentWorkspaceBindingRecord("binding.one", workspace.WorkspaceId, AgentRpcContractIds.ExecutionTarget,
            "local", "primary", true, 0, now, now);
        var target = new AgentExecutionTargetDescriptor("local", "local", "Local", null, true, true) { Facets = ["execution-scope"] };
        var handle = new AgentRpcProviderHandle("sunder.package.agent.execution.local", "2.0.0", "local.execution.target",
            AgentRpcContractIds.ExecutionTarget, "1.0.0", new string('a', 64), new string('b', 64), Guid.NewGuid(), 7, 11, "test.target.exact");
        return new Resolver
        {
            Workspace = workspace,
            Resolution = new(workspace, binding, target, new("Local", ["/work/one"], "/work/one"),
                new Runtime.Rpc.GeneratedExecutionTargetClient(client, new(handle.PackageId, handle.PackageVersion, handle.ProviderId,
                    handle.ContractId, handle.ContractVersion, handle.DescriptorSha256, handle.AbiSha256, handle.ActivationId,
                    handle.ActivationEpoch, handle.SessionGeneration, new(handle.EndpointReference), 1, SunderRpcProviderState.Active))) { ExecutionTargetHandle = handle },
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
