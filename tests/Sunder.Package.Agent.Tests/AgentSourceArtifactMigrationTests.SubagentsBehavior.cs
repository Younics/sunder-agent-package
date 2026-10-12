using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;
using Xunit;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Tests;

extern alias SubagentsWorker;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifySubagentsRoles(string worker)
    {
        var load = new AssemblyLoadContext("subagents-role-local", isCollectible: true);
        try
        {
            using var workerStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "worker"), "Sunder.Package.Agent.Subagents.dll"));
            var runtime = load.LoadFromStream(workerStream);
            using var ownerStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(Path.Combine(Path.GetDirectoryName(worker)!, "Worker"), "worker"), "Sunder.Package.Agent.dll"));
            var agentOwner = load.LoadFromStream(ownerStream);
            await VerifySubagentRunControlConsumer(agentOwner);
            await VerifySubagentRuntimeConsumers(runtime, agentOwner);
            await VerifySubagentCatalogConsumers(runtime);
            using var appStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "app"), "Sunder.Package.Agent.Subagents.App.dll"));
            var presentation = load.LoadFromStream(appStream);
            AssertSubagentsSemanticOwnership(runtime, app: false);
            AssertSubagentsSemanticOwnership(presentation, app: true);
            Assert.NotSame(runtime.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentTurnRecord", true), presentation.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentTurnRecord", true));
            foreach (var assembly in new[] { runtime, presentation })
            {
                Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Agent.Subagents.Protocol");
                foreach (var name in new[] { "Sunder.Package.Agent.Subagents.Models.SubagentRecord", "Sunder.Package.Agent.Subagents.Runtime.SubagentProjectionContent",
                             "Sunder.Package.Agent.Shared.PackageViews.TranscriptPageCursor" })
                    Assert.Same(assembly, assembly.GetType(name, true)!.Assembly);
            }
            Assert.DoesNotContain(presentation.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Subagents" or "Sunder.Sdk.Worker");
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Subagents.Runtime.SubagentAppRuntimeGateway"));
            foreach (var type in new[] { "Services.SubagentStore", "Services.SubagentService", "Services.SubagentFeature", "Runtime.SubagentRuntimeHandler" })
                Assert.Null(presentation.GetType("Sunder.Package.Agent.Subagents." + type));
            using var package = RegressionTestPackageScope.Create();
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.subagents");
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Subagents.Runtime.SubagentsWorkerComposition"));
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Subagents.Runtime.SubagentsWorkerBindings"));
            var (options, workerLifetime, runtimeServices) = CreateGeneratedWorker(runtime, package.Context, rpc);
            await using var owned = workerLifetime;
            Assert.Empty(Directory.GetFiles(package.RootPath, "*", SearchOption.AllDirectories));
            Assert.Equal(0, state.DiscoveryCount);
            await options.OnActivated!(null!, CancellationToken.None);
            var registrations = options.Providers.ToDictionary(value => value.ProviderId);
            Assert.Equal(new[] { "subagents.behavior.loop", "subagents.management", "subagents.prompt.context", "subagents.selectable.capabilities", "subagents.stack", "subagents.tools" }, registrations.Keys.Order(StringComparer.Ordinal));
            foreach (var provider in options.Providers)
            {
                var descriptor = provider.ProviderId == "subagents.management" ? CurrentPrivateRpcDescriptors.Get("sunder.package.agent.subagents.owner")
                    : provider.ProviderId == "subagents.stack" ? SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(
                        BuilderSourceMigrationTests.FindRepository(), "../sunder-core/languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/Contracts/sunder.stack.contributor.rpc.json")))
                    : CurrentPublicRpcDescriptors.Get(provider.ContractId);
                Assert.Equal(descriptor.Sha256, provider.DescriptorSha256);
                Assert.Equal(descriptor.AbiSha256, provider.AbiSha256);
                state.Add("sunder.package.agent.subagents", descriptor, provider.Handler, provider.Handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            var catalogPeers = AddCatalogPeers(state);
            var appRoot = Directory.CreateDirectory(Path.Combine(package.RootPath, "app-workspace")).FullName;
            var context = new TestPackageContext("sunder.package.agent.subagents", appRoot, storage: new TestPackageStorageContext(package.Context.Storage.Files,
                package.Context.Storage.State, new TestPackageRoleLocalWorkspace(appRoot)));
            await using var appServices = CreateGeneratedApp(presentation, context, rpc);
            // The shared consumer helper may be present, but this family uses its
            // owner gateway and must not start an unrelated catalog lifetime.
            Assert.Null(presentation.GetType("Sunder.Package.Agent.Protocol.AgentRpcCatalog"));
            var gateway = appServices.GetRequiredService(presentation.GetType("Sunder.Package.Agent.Subagents.Runtime.SubagentAppRuntimeGateway", true)!);
            Assert.Empty(((IEnumerable)(await Call("ListSubagentsAsync", CancellationToken.None))!).Cast<object>());
            var created = (await Call("CreateSubagentAsync", "Artifact Subagent", CancellationToken.None))!;
            Assert.Same(presentation, created.GetType().Assembly);
            var id = (string)created.GetType().GetProperty("SubagentId")!.GetValue(created)!;
            var save = gateway.GetType().GetMethod("SaveSubagentAsync")!;
            var request = JsonSerializer.SerializeToElement(new { subagentId = id, displayName = " Saved λ ", description = "Independent artifact task", instructions = "Return a focused result.",
                assignments = new[] { new { kind = "tool", capabilityId = "fixture.alias", sourceId = "fixture.tools" } } })
                .Deserialize(save.GetParameters()[0].ParameterType, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var saved = (await Call("SaveSubagentAsync", request, CancellationToken.None))!;
            Assert.Equal("Saved λ", saved.GetType().GetProperty("DisplayName")!.GetValue(saved));
            Assert.Single(((IEnumerable)(await Call("ListSubagentsAsync", CancellationToken.None))!).Cast<object>());
            Assert.Single(((IEnumerable)(await Call("ListChatProvidersAsync", CancellationToken.None))!).Cast<object>());
            var modelCatalog = (await Call("LoadChatModelsAsync", "fixture.chat", CancellationToken.None))!;
            var modelOptions = ((IEnumerable)modelCatalog.GetType().GetProperty("Models")!.GetValue(modelCatalog)!).Cast<object>().ToArray();
            Assert.Equal("fixture/new", modelOptions[0].GetType().GetProperty("Id")!.GetValue(modelOptions[0]));
            Assert.Single(((IEnumerable)(await Call("ListLocalToolsAsync", CancellationToken.None))!).Cast<object>());
            Assert.Single(((IEnumerable)(await Call("ListPackageCapabilitiesAsync", CancellationToken.None))!).Cast<object>());
            var storeType = runtime.GetType("Sunder.Package.Agent.Subagents.Services.SubagentStore", true)!;
            var persisted = Assert.Single(((IEnumerable)storeType.GetMethod("List")!.Invoke(runtimeServices.GetRequiredService(storeType), null)!).Cast<object>());
            Assert.Same(runtime, persisted.GetType().Assembly);
            Assert.Equal(id, persisted.GetType().GetProperty("SubagentId")!.GetValue(persisted));
            var invalid = JsonSerializer.SerializeToElement(new { subagentId = id, displayName = "Saved", description = " " })
                .Deserialize(save.GetParameters()[0].ParameterType, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var domainError = await Assert.ThrowsAsync<SunderRpcException>(() => Call("SaveSubagentAsync", invalid, CancellationToken.None));
            Assert.Equal("subagents.owner.operation-failed", domainError.Error.Code);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call("DeleteSubagentAsync", id, cancelled.Token));
                Assert.Single(((IEnumerable)(await Call("ListSubagentsAsync", CancellationToken.None))!).Cast<object>());
            }

            var now = DateTimeOffset.UtcNow;
            var profile = new AgentProfileRecord("profile.subagents", "Subagents", null, null, "chat.provider", "chat/model", null, null, now, now,
                SelectableCapabilityAssignments: [new("subagent", id, "sunder.package.agent.subagents")], BehaviorLoopId: "orchestrated", BehaviorLoopSourceId: "sunder.package.agent.subagents");
            var runtimeCatalog = new MemoryConsumerRuntime(profile);
            AddSubagentSourceProvider(state, agentOwner, "RuntimeCatalogService", runtimeCatalog, RegressionTestBindings.RuntimeCatalog);
            var child = new ArtifactChildExecutor();
            AddSubagentSourceProvider(state, agentOwner, "ChildRunExecutorService", child, RegressionTestBindings.ChildRunExecutor);
            await using var publicReceiver = new GeneratedServiceTestScope(rpc);
            var capabilityClient = Assert.Single((await publicReceiver.Providers<AgentServices.ISelectableCapabilityProviderService>().ListAsync()).Providers,
                value => value.ProviderId == "subagents.selectable.capabilities").Service;
            var toolClient = Assert.Single((await publicReceiver.Providers<AgentServices.IToolSourceService>().ListAsync()).Providers,
                value => value.ProviderId == "subagents.tools").Service;
            var promptClient = Assert.Single((await publicReceiver.Providers<AgentServices.IPromptContextContributorService>().ListAsync()).Providers,
                value => value.ProviderId == "subagents.prompt.context").Service;
            Assert.Equal(id, Assert.Single(await capabilityClient.ListAsync(profile)).CapabilityId);
            var sourceContext = new AgentToolSourceContext(null, profile);
            var listed = await toolClient.ListToolsAsync(sourceContext);
            Assert.Equal(new[] { "task", "delegate_tasks" }, listed.Select(value => value.ToolId));
            catalogPeers.Tools.ReadOnly = false;
            var mutating = await toolClient.ListToolsAsync(sourceContext);
            Assert.Equal("task", Assert.Single(mutating).ToolId);
            catalogPeers.Tools.ReadOnly = true;
            Assert.Equal(AgentToolReadinessStatus.Ready, (await toolClient.GetReadinessAsync("task", sourceContext))!.Status);
            var runId = Guid.NewGuid();
            var workspace = new AgentWorkspaceRecord("workspace.subagents", "Subagents", null, now, now);
            var execution = new AgentToolExecutionContext(runtimeCatalog.Session.SessionId, profile.ProfileId, workspace,
                RunId: runId, RunRevision: 3, ToolCallId: "parent.call");
            var arguments = JsonSerializer.Serialize(new { prompt = "Read the artifact", subagent_type = id, task_id = "artifact.task", description = "Artifact child" });
            var invocation = new AgentToolRequest("task", arguments);
            Assert.Null(await toolClient.PreflightAsync(execution, invocation));
            Assert.Null(await toolClient.PermissionAsync(execution, invocation));
            var executed = await toolClient.ExecuteAsync(execution, invocation);
            Assert.False(executed.IsError);
            Assert.Equal("Child artifact result", executed.Content);
            var delegated = Assert.Single(child.Requests);
            Assert.Equal(runId, delegated.ParentRunId);
            Assert.Equal(3, delegated.ParentRunRevision);
            Assert.Equal(workspace.WorkspaceId, delegated.WorkspaceId);
            Assert.Equal("artifact.task", delegated.TaskId);
            Assert.Equal("chat.provider", delegated.ChildProfile.ChatProviderId);
            Assert.True(delegated.ChildProfile.IsInternal);
            Assert.Equal(AgentBehaviorLoopIds.Default, delegated.ChildProfile.BehaviorLoopId);
            var view = await toolClient.PresentationAsync(new AgentToolPresentationRequest("task", arguments, "Done", "Child artifact result",
                executed.StructuredPayloadJson, null, false, null, null));
            Assert.Contains("Saved λ", view!.HeaderText!, StringComparison.Ordinal);
            var batch = await toolClient.ExecuteAsync(execution, new("delegate_tasks",
                JsonSerializer.Serialize(new { tasks = new[] { new { prompt = "First", subagent_type = id, task_id = "first" }, new { prompt = "Second", subagent_type = id, task_id = "second" } } })));
            Assert.False(batch.IsError);
            Assert.Equal(3, child.Requests.Count);
            var promptSession = new AgentSessionContextRecord(runtimeCatalog.Session.SessionId, profile.ProfileId, profile.DisplayName, "Subagents", AgentSessionState.Active, null);
            var promptRun = new AgentRunContextRecord(runId, 3, AgentRunStatus.Running, false, now);
            var prompt = await promptClient.ContributeAsync(new AgentPromptContextRequest(promptSession, promptRun,
                new(promptSession, promptRun, "Delegate a task", null), [], [], new("GeneralFact", "subagents", MaxEntryCount: 2, MaxChars: 1000))
            {
                Profile = profile,
            });
            Assert.Contains("Independent artifact task", prompt!.Blocks[0].Content, StringComparison.Ordinal);
            var items = await Rpc("subagents.stack", "stack-contributor", "list-export-items", new { packageId = "sunder.package.agent.subagents" });
            Assert.Equal(id, Assert.Single(items.GetProperty("items").EnumerateArray()).GetProperty("itemId").GetString());
            var exported = await Rpc("subagents.stack", "stack-contributor", "export", new StackExportRequest([new(id)]));
            Assert.Contains(exported.GetProperty("packageRequirements").EnumerateArray(), value => value.GetProperty("packageId").GetString() == "example.catalog");
            await VerifySubagentsBehaviorLoop(state, rpc, registrations["subagents.behavior.loop"], agentOwner);

            var available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.GetType().GetEvent("OwnerStreamAvailabilityChanged")!.AddEventHandler(gateway, (Action<bool>)(value => { if (value) available.TrySetResult(); }));
            await Call("InitializeAsync", CancellationToken.None);
            await available.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var sessions = (await Call("ListSessionsAsync", CancellationToken.None))!;
            Assert.Same(presentation, sessions.GetType().Assembly);
            var session = Assert.Single(((IEnumerable)sessions.GetType().GetProperty("Sessions")!.GetValue(sessions)!).Cast<object>());
            Assert.Same(presentation, session.GetType().Assembly);
            Assert.Equal(runtimeCatalog.Session.SessionId, (Guid)session.GetType().GetProperty("SessionId")!.GetValue(session)!);
            var transcript = (await Call("ListRecentTurnsAsync", runtimeCatalog.Session.SessionId, 10, CancellationToken.None))!;
            Assert.Same(presentation, transcript.GetType().Assembly);
            Assert.Empty((IEnumerable)transcript.GetType().GetProperty("Turns")!.GetValue(transcript)!);
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.GetType().GetEvent("SubagentsChanged")!.AddEventHandler(gateway, (Action)(() => changed.TrySetResult()));
            var endpoint = Assert.Single((await rpc.DiscoverAsync(RegressionTestBindings.SelectableCapabilityProviders)).Providers, value => value.ProviderId == "subagents.selectable.capabilities").Endpoint;
            using var lifetime = new CancellationTokenSource();
            await using var subscription = rpc.SubscribeAsync(endpoint, "selectable-capability-provider", "watch", JsonSerializer.SerializeToElement(new { }), cancellationToken: lifetime.Token).GetAsyncEnumerator();
            var next = subscription.MoveNextAsync().AsTask();
            await Call("DeleteSubagentAsync", id, CancellationToken.None);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
            lifetime.Cancel();
            Assert.Empty(((IEnumerable)(await Call("ListSubagentsAsync", CancellationToken.None))!).Cast<object>());
            var management = registrations["subagents.management"];
            await state.RetireAsync(management.Handler).WaitAsync(TimeSpan.FromSeconds(5));
            var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() => Call("ListSubagentsAsync", CancellationToken.None));
            Assert.Equal(SunderRpcErrorKind.NotFound, unavailable.Error.Kind);
            Assert.Equal("rpc.provider-not-found", unavailable.Error.Code);
            state.Add("sunder.package.agent.subagents", CurrentPrivateRpcDescriptors.Get("sunder.package.agent.subagents.owner"), management.Handler, management.Handler, Noop.Instance, management.ProviderId, "2.0.0");
            Assert.Empty(((IEnumerable)(await Call("ListSubagentsAsync", CancellationToken.None))!).Cast<object>());
            await ((IAsyncDisposable)gateway).DisposeAsync();
            Assert.True(((Task)gateway.GetType().GetProperty("OwnerObservationTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(gateway)!).IsCompleted);
            await owned.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, catalogPeers.Capabilities.Subscribers);

            async Task<object?> Call(string method, params object?[] args)
            {
                var operation = (Task)gateway.GetType().GetMethod(method)!.Invoke(gateway, args)!;
                await operation.WaitAsync(TimeSpan.FromSeconds(10));
                return operation.GetType().GetProperty("Result")?.GetValue(operation);
            }
            async Task<JsonElement> Rpc(string id, string serviceId, string method, object value)
            {
                var provider = registrations[id];
                var selected = Assert.Single((await rpc.DiscoverAsync(provider.ContractId)).Providers, value => value.ProviderId == id);
                return await rpc.InvokeAsync(selected.Endpoint, serviceId, method, JsonSerializer.SerializeToElement(value, MemoryProviderJson)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally { load.Unload(); }
    }

    private static async Task VerifySubagentsBehaviorLoop(RegressionTestRpcState state, ISunderRpcClient rpc, SunderWorkerProviderRegistration loop, Assembly agentOwner)
    {
        var registry = new AgentRoleRunControls(agentOwner);
        var host = new AgentPublicSourceMigrationTests.FullRunControlProbe();
        await using var receiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, "sunder.package.agent.subagents"));
        var runControl = AgentRoleBoundary.Create(agentOwner.GetType("Sunder.Package.Agent.Services.AgentRunControlService", true)!, registry.Value);
        var registration = receiver.Register(runControl, RegressionTestBindings.RunControls);
        state.Add("sunder.package.agent", CurrentPublicRpcDescriptors.Get(RegressionTestBindings.RunControls), runControl,
            registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        await using var loopReceiver = new GeneratedServiceTestScope(rpc);
        var selected = Assert.Single((await loopReceiver.Providers<AgentServices.IBehaviorLoopService>().ListAsync()).Providers, value => value.ProviderId == loop.ProviderId).Service;
        var runControlReference = Assert.Single((await loopReceiver.Providers<AgentServices.IRunControlService>().ListAsync()).Providers,
            value => value.ProviderId == registration.ProviderId).Reference;
        using var lease = registry.Open(host, "sunder.package.agent.subagents", out var invocationId);
        var request = new AgentPublicSourceMigrationTests.BehaviorLoopRunRequest(invocationId, host.Context);
        var result = await Invoke(request);
        Assert.Equal("waiting-for-approval", result.GetProperty("completionKind").GetString());
        Assert.Equal(host.Context.RunRevision, result.GetProperty("checkpoint").GetProperty("runRevision").GetInt64());
        Assert.Equal(host.Context.UserMessage, host.Arguments.GetProperty("context").GetProperty("userMessage").GetString());
        var calls = host.Calls;
        using (registry.Open(host, "different.caller", out var wrongInvocation))
            await Assert.ThrowsAsync<SunderRpcException>(() => Invoke(request with { InvocationId = wrongInvocation }));
        Assert.Equal(calls, host.Calls);
        host.Block = true;
        using var cancelled = new CancellationTokenSource();
        var inFlight = Invoke(request, cancelled.Token);
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight.WaitAsync(TimeSpan.FromSeconds(5)));
        host.Block = false;
        await state.RetireAsync(runControl).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<SunderRpcException>(() => Invoke(request));

        async Task<JsonElement> Invoke(AgentPublicSourceMigrationTests.BehaviorLoopRunRequest value, CancellationToken token = default)
            => JsonSerializer.SerializeToElement(await selected.RunAsync(value.Context, value.InvocationId, runControlReference, token), MemoryProviderJson);
    }

    private sealed class ArtifactChildExecutor : IAgentChildRunExecutor
    {
        internal List<AgentChildRunRequest> Requests { get; } = [];
        internal bool Block;
        internal bool NullFields;
        internal AgentRunStatus Status = AgentRunStatus.Completed;
        internal Action? AfterReply;
        internal TaskCompletionSource Started { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<AgentChildRunResult> RunChildAsync(AgentChildRunRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Requests) Requests.Add(request);
            if (Block) { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            AfterReply?.Invoke();
            return new AgentChildRunResult(Guid.NewGuid(), Status, "Done", NullFields ? null : "Child artifact result", NullFields ? null : request.Title);
        }
    }
}
