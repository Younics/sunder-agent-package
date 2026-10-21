using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias McpWorker;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using McpWorker::Sunder.Package.Agent.Mcp.Runtime;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyMcpRoles(string worker)
    {
        var wireJson = new JsonSerializerOptions(MemoryProviderJson)
        { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };
        var load = new AssemblyLoadContext("mcp-role-local", isCollectible: true);
        try
        {
            using var workerStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "worker"), "Sunder.Package.Agent.Mcp.dll"));
            var runtime = load.LoadFromStream(workerStream);
            await VerifyGeneratedMcpComposition(runtime);
            var comparison = await BuildRoleProbes(worker, runtime, fixture: null,
                Directory.GetFiles(SingleProjectRoleOutput(worker, "worker"), "*.dll"),
                assemblyName: "Sunder.Package.Agent.Tests", output: SingleProjectRoleOutput(worker, "worker"), controlledComposition: true);
            using var ownerStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(Path.Combine(Path.GetDirectoryName(worker)!, "Worker"), "worker"), "Sunder.Package.Agent.dll"));
            var agentOwner = load.LoadFromStream(ownerStream);
            await VerifyMcpWorkspaceConsumer(runtime, agentOwner);
            using var appStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "app"), "Sunder.Package.Agent.Mcp.App.dll"));
            var presentation = load.LoadFromStream(appStream);
            AssertMcpSemanticOwnership(runtime, app: false);
            AssertMcpSemanticOwnership(presentation, app: true);
            Assert.NotSame(runtime.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentToolDescriptor", true), presentation.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentToolDescriptor", true));
            foreach (var assembly in new[] { runtime, presentation })
            {
                Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Agent.Mcp.Shared");
                foreach (var name in new[] { "Sunder.Package.Agent.Mcp.ConfiguredMcpServerRecord", "Sunder.Generated.Packages.Younics_Ai_Agent_Mcp_Services.IManagementService", "Sunder.Package.Agent.Mcp.Runtime.McpManagementContent" })
                    Assert.Same(assembly, assembly.GetType(name, true)!.Assembly);
            }
            Assert.DoesNotContain(presentation.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Mcp" or "Sunder.Sdk.Worker"
                or "ModelContextProtocol" or "ModelContextProtocol.Core");
            using var workspace = RegressionTestPackageScope.Create();
            var callbacks = new AppCallbackProbe("younics.ai.agent.mcp", "mcp.oauth.v1", new Dictionary<string, string> { ["serverId"] = "artifact-mcp" });
            var context = new TestPackageContext("younics.ai.agent.mcp", workspace.RootPath, version: "2.0.0", storage: workspace.Context.Storage, callbacks: callbacks);
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var rpc = new RegressionTestRpcClient(state, "younics.ai.agent.mcp");
            var syncType = runtime.GetType("Sunder.Package.Agent.Mcp.Services.McpSunderConfigurationSyncService", true)!;
            Action<IServiceCollection> isolatePaths = services => services.AddSingleton(syncType, services => Activator.CreateInstance(syncType,
                services.GetRequiredService(runtime.GetType("Sunder.Package.Agent.Mcp.Services.McpEcosystemConfigurationImporter", true)!),
                services.GetRequiredService(typeof(Sunder.Sdk.Services.IServiceProviders<>).MakeGenericType(
                    runtime.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService", true)!)), workspace.RootPath, workspace.RootPath)!);
            var options = CreateControlledGeneratedWorker(comparison, context, rpc, isolatePaths).Options;
            await using var composition = Assert.IsType<SunderWorkerComposition>(options.Composition);
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Mcp.Runtime.LegacyMcpWorkspaceCatalog"));
            Assert.Equal(0, state.DiscoveryCount);
            Assert.Equal("mcp.oauth.v1", Assert.Single(options.CallbackHandlers).CallbackHandlerId);
            await ActivateGeneratedComposition(options);
            var registrations = options.Providers.ToDictionary(value => value.ProviderId);
            Assert.Equal(new[] { "mcp.management", "mcp.selectable.capabilities", "mcp.session.cleaner", "mcp.stack", "mcp.tools", "mcp.tools.observed" }, registrations.Keys.Order(StringComparer.Ordinal));
            foreach (var provider in options.Providers)
            {
                var descriptor = provider.ProviderId == "mcp.management" ? CurrentPrivateRpcDescriptors.Get("younics.ai.agent.mcp.management")
                    : provider.ProviderId == "mcp.stack" ? SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(BuilderSourceMigrationTests.FindRepository(),
                        "../sunder-core/languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/Contracts/sunder.stack.contributor.rpc.json")))
                    : CurrentPublicRpcDescriptors.Get(provider.ContractId);
                Assert.Equal(descriptor.Sha256, provider.DescriptorSha256); Assert.Equal(descriptor.AbiSha256, provider.AbiSha256);
                state.Add("younics.ai.agent.mcp", descriptor, provider.Handler, provider.Handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            await using var appProvider = CreateGeneratedApp(presentation, context, rpc);
            var gateway = appProvider.GetRequiredService(presentation.GetType("Sunder.Package.Agent.Mcp.Runtime.McpAppRuntimeGateway", true)!);
            await Call("InitializeAsync", CancellationToken.None);
            Assert.Empty(((IEnumerable)(await Call("ListAsync", CancellationToken.None))!).Cast<object>());
            await using var peer = new McpLoopbackPeer();
            var document = JsonSerializer.Serialize(new { type = "remote", url = peer.Endpoint, headers = new Dictionary<string, string> { ["X-Fixture-Secret"] = "test-secret" } });
            var parsed = gateway.GetType().GetMethod("Parse")!.Invoke(gateway, ["artifact-mcp", "Artifact MCP", document, null])!;
            Assert.Same(presentation, parsed.GetType().Assembly);
            await Call("SaveAsync", parsed, CancellationToken.None);
            var server = Assert.Single(((IEnumerable)(await Call("ListAsync", CancellationToken.None))!).Cast<object>());
            Assert.Same(presentation, server.GetType().Assembly);
            Assert.DoesNotContain("test-secret", (string)(await Call("LoadDocumentAsync", "artifact-mcp", CancellationToken.None))!, StringComparison.Ordinal);
            var catalogType = runtime.GetType("Sunder.Package.Agent.Mcp.Services.McpServerCatalogService", true)!;
            var catalog = composition.Services.GetRequiredService(catalogType);
            var list = (Task)catalogType.GetMethod("ListServersAsync")!.Invoke(catalog, [CancellationToken.None])!;
            await list;
            Assert.Same(runtime, Assert.Single(((IEnumerable)list.GetType().GetProperty("Result")!.GetValue(list)!).Cast<object>()).GetType().Assembly);
            var missing = await Assert.ThrowsAsync<SunderRpcException>(() => Call("LoadDocumentAsync", "missing", CancellationToken.None));
            Assert.Equal("mcp.management.failed", missing.Error.Code);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call("DeleteAsync", "artifact-mcp", cancelled.Token));
                Assert.Single(((IEnumerable)(await Call("ListAsync", CancellationToken.None))!).Cast<object>());
            }
            var session = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var profile = new AgentProfileRecord("profile.mcp", "MCP", null, null, null, null, null, null, now, now,
                SelectableCapabilityAssignments: [new(AgentProfileSelectableCapabilityKinds.ToolGroup, "artifact-mcp", "mcp")]);
            var capabilities = await Rpc("mcp.selectable.capabilities", "selectable-capability-provider", "list", profile);
            Assert.Equal("artifact-mcp", Assert.Single(capabilities.EnumerateArray()).GetProperty("capabilityId").GetString());
            var sourceWire = new { sessionId = session, profile, workspace = (object?)null, executionBinding = (object?)null,
                executionTarget = (object?)null, executionTargetConfigurationGeneration = (string?)null };
            var tools = await Rpc("mcp.tools", "tool-source", "list-tools", sourceWire);
            var toolId = Assert.Single(tools.EnumerateArray()).GetProperty("toolId").GetString()!;
            var ready = await Rpc("mcp.tools", "tool-source", "get-readiness", new { toolId, context = sourceWire });
            Assert.Equal("ready", ready.GetProperty("status").GetString());
            var execution = new { sessionId = session, profileId = profile.ProfileId, workspace = (object?)null, executionBinding = (object?)null,
                runId = (Guid?)null, runRevision = (long?)null, userTurnId = (Guid?)null, toolCallId = (string?)null, transcriptEpoch = 0,
                approvedResourceCapabilities = Array.Empty<string>(), resourceOperation = (object?)null,
                executionTarget = (object?)null, executionTargetConfigurationGeneration = (string?)null,
                allowOutsideConfiguredScope = false, approvedResourceClaims = Array.Empty<object>(), approvedResourceReferences = Array.Empty<string>() };
            object Request(string message) => new { context = execution, request = new { toolId, argumentsJson = JsonSerializer.Serialize(new { message }) } };
            var executed = await Rpc("mcp.tools", "tool-source", "execute", Request("normal"));
            Assert.False(executed.GetProperty("isError").GetBoolean());
            Assert.Contains("artifact:normal", executed.GetProperty("content").GetString()!, StringComparison.Ordinal);
            var observedEndpoint = (await Select("mcp.tools.observed")).Endpoint;
            var before = peer.ToolCalls;
            await using (var observed = rpc.SubscribeAsync(observedEndpoint, "tool-execution-observed", "execute", JsonSerializer.SerializeToElement(Request("observed"), wireJson)).GetAsyncEnumerator())
            {
                Assert.True(await observed.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal("dispatched", observed.Current.GetProperty("kind").GetString());
                Assert.Equal(before, peer.ToolCalls);
                Assert.True(await observed.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Contains("artifact:observed", observed.Current.GetProperty("result").GetProperty("content").GetString()!, StringComparison.Ordinal);
                Assert.False(await observed.MoveNextAsync());
            }
            before = peer.ToolCalls;
            await using (var abandoned = rpc.SubscribeAsync(observedEndpoint, "tool-execution-observed", "execute", JsonSerializer.SerializeToElement(Request("abandoned"), wireJson)).GetAsyncEnumerator())
            {
                Assert.True(await abandoned.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal("dispatched", abandoned.Current.GetProperty("kind").GetString());
            }
            Assert.Equal(before, peer.ToolCalls);
            var agentRpc = new RegressionTestRpcClient(state, "younics.ai.agent");
            await using var generatedReceiver = new GeneratedServiceTestScope(agentRpc);
            var generatedTools = Assert.Single((await generatedReceiver.Providers<AgentServices.IToolSourceService>().ListAsync()).Providers,
                provider => provider.ProviderId == "mcp.tools").Service;
            var generatedObserved = Assert.Single((await generatedReceiver.Providers<AgentServices.IToolExecutionObservedService>().ListAsync()).Providers,
                provider => provider.ProviderId == "mcp.tools.observed").Service;
            var sourceContext = new AgentToolSourceContext(session, profile);
            Assert.Equal(toolId, Assert.Single(await generatedTools.ListToolsAsync(sourceContext)).ToolId);
            Assert.Equal(AgentToolReadinessStatus.Ready, (await generatedTools.GetReadinessAsync(toolId, sourceContext))!.Status);
            var generatedContext = new AgentToolExecutionContext(session, profile.ProfileId);
            Assert.Contains("artifact:generated", (await generatedTools.ExecuteAsync(generatedContext, new(toolId, "{\"message\":\"generated\"}"))).Content!, StringComparison.Ordinal);
            var generatedDispatches = 0; before = peer.ToolCalls;
            var generatedEvents = new List<JsonElement>();
            await foreach (var item in generatedObserved.ExecuteAsync(generatedContext, new(toolId, "{\"message\":\"generated-observed\"}")))
            {
                if (item is AgentToolDispatchedEvent) { Assert.Equal(before, peer.ToolCalls); generatedDispatches++; }
                generatedEvents.Add(JsonSerializer.SerializeToElement(item, item.GetType(), wireJson));
            }
            Assert.Equal(1, generatedDispatches);
            Assert.Contains(generatedEvents, item => item.TryGetProperty("result", out var result)
                && result.GetProperty("content").GetString()!.Contains("artifact:generated-observed", StringComparison.Ordinal));
            before = peer.ToolCalls;
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var item in generatedObserved.ExecuteAsync(generatedContext, new(toolId, "{\"message\":\"must-not-dispatch\"}")))
                {
                    Assert.IsType<AgentToolDispatchedEvent>(item);
                    throw new InvalidOperationException("Dispatch acknowledgement rejected");
                }
            });
            Assert.Equal(before, peer.ToolCalls);
            using (var agentPackage = RegressionTestPackageScope.Create())
            {
                await using var host = new AgentPromptHost(agentOwner, agentPackage.Context, agentRpc);
                var profileService = host.Services.GetRequiredService(agentOwner.GetType("Sunder.Package.Agent.Services.AgentProfileService", true)!);
                SurfaceCall(profileService, "ImportProfile", profile with { ProfileId = host.Profile.ProfileId });
                var runtimeCatalog = host.Services.GetRequiredService(agentOwner.GetType("Sunder.Package.Agent.Services.AgentRuntimeCatalog", true)!);
                AddSubagentSourceProvider(state, agentOwner, "RuntimeCatalogService", runtimeCatalog, RegressionTestBindings.RuntimeCatalog);
                var agentCatalogHandler = runtimeCatalog;
                var toolService = host.Services.GetRequiredService(agentOwner.GetType("Sunder.Package.Agent.Services.AgentToolService", true)!);
                before = peer.ToolCalls; generatedDispatches = 0;
                var routed = await AgentRoleBoundary.As<Task<AgentToolResult>>(AgentRoleBoundary.Call(toolService, "ExecuteAsync",
                    toolId, "{\"message\":\"agent-routed\"}", host.Session.SessionId, host.Profile.ProfileId, host.Workspace, false,
                    null, null, null, null, null, null, null, CancellationToken.None, null, null, null, null, null, false,
                    (Action)(() => { Assert.Equal(before, peer.ToolCalls); generatedDispatches++; }))!);
                Assert.False(routed.IsError, routed.Content); Assert.Equal(1, generatedDispatches);
                Assert.Contains("artifact:agent-routed", routed.Content!, StringComparison.Ordinal);
                await Rpc("mcp.session.cleaner", "session-cleaner", "delete-session", host.Session.SessionId);
                await state.RetireAsync(agentCatalogHandler).WaitAsync(TimeSpan.FromSeconds(5));
            }
            using (var cancelled = new CancellationTokenSource())
            {
                var target = await Select("mcp.tools");
                var pending = rpc.InvokeAsync(target.Endpoint, "tool-source", "execute", JsonSerializer.SerializeToElement(Request("wait"), wireJson), cancellationToken: cancelled.Token).AsTask();
                await peer.BlockedCall.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            var managerType = runtime.GetType("Sunder.Package.Agent.Mcp.Services.McpClientConnectionManager", true)!;
            var manager = composition.Services.GetRequiredService(managerType);
            await Rpc("mcp.session.cleaner", "session-cleaner", "delete-session", session);
            Assert.Equal(0, managerType.GetProperty("CachedConnectionCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager));
            var items = await Rpc("mcp.stack", "stack-contributor", "list-export-items", new { packageId = "younics.ai.agent.mcp" });
            Assert.Single(items.GetProperty("items").EnumerateArray());
            using (var cancellation = new CancellationTokenSource())
            {
                var authorization = Call("AuthorizeAsync", server, cancellation.Token);
                await callbacks.Polled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, callbacks.Starts); Assert.Equal(1, callbacks.Opens);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authorization);
                await callbacks.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, callbacks.Cancellations);
            }
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.GetType().GetEvent("ServersChanged")!.AddEventHandler(gateway, (Action)(() => changed.TrySetResult()));
            await Call("DeleteAsync", "artifact-mcp", CancellationToken.None);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(((IEnumerable)(await Call("ListAsync", CancellationToken.None))!).Cast<object>());
            await state.RetireAsync(registrations["mcp.management"].Handler).WaitAsync(TimeSpan.FromSeconds(5));
            var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() => Call("ListAsync", CancellationToken.None));
            Assert.Equal(SunderRpcErrorKind.NotFound, unavailable.Error.Kind);
            Assert.Equal("rpc.provider-not-found", unavailable.Error.Code);
            await ((IAsyncDisposable)gateway).DisposeAsync();
            Assert.True(((Task)gateway.GetType().GetProperty("ObservationTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gateway)!).IsCompleted);
            await composition.DisposeAsync();

            async Task<object?> Call(string method, params object?[] args)
            {
                var operation = (Task)gateway.GetType().GetMethod(method)!.Invoke(gateway, args)!;
                await operation.WaitAsync(TimeSpan.FromSeconds(15));
                return operation.GetType().GetProperty("Result")?.GetValue(operation);
            }
            async Task<SunderRpcProviderSnapshot> Select(string id) => Assert.Single((await rpc.DiscoverAsync(registrations[id].ContractId)).Providers, provider => provider.ProviderId == id);
            async Task<JsonElement> Rpc(string id, string service, string method, object value)
                => await rpc.InvokeAsync((await Select(id)).Endpoint, service, method, JsonSerializer.SerializeToElement(value, wireJson)).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { load.Unload(); }
    }
}
