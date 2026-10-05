using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
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

extern alias SkillsWorker;
using SkillsWorker::Sunder.Package.Agent.Skills.Runtime;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifySkillsRoles(string worker)
    {
        var load = new AssemblyLoadContext("skills-role-local", isCollectible: true);
        try
        {
            using var workerStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "worker"), "Sunder.Package.Agent.Skills.dll"));
            var runtime = load.LoadFromStream(workerStream);
            var probes = await BuildRoleProbes(worker, runtime, "SkillsRoleLocalProbes.cs.txt", output: SingleProjectRoleOutput(worker, "worker"));
            using var agentStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(Path.Combine(Path.GetDirectoryName(worker)!, "Worker"), "worker"), "Sunder.Package.Agent.dll"));
            var agentOwner = load.LoadFromStream(agentStream);
            await VerifyAgentSelectableConsumers(agentOwner, runtime, probes);
            await VerifySkillsConsumers(runtime.GetType("Sunder.Package.Agent.Skills.Rpc.SkillsRuntimeBindings", true)!);
            using var appStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "app"), "Sunder.Package.Agent.Skills.App.dll"));
            var presentation = load.LoadFromStream(appStream);
            AssertSkillsSemanticOwnership(runtime, app: false);
            AssertSkillsSemanticOwnership(presentation, app: true);
            Assert.NotSame(runtime.GetType("Sunder.Package.Agent.Skills.Services.InstalledSkillRecord"), presentation.GetType("Sunder.Package.Agent.Skills.Services.InstalledSkillRecord"));
            foreach (var assembly in new[] { runtime, presentation })
            {
                Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Agent.Skills.Protocol");
                Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Skills.Services.InstalledSkillRecord", true)!.Assembly);
            }
            Assert.DoesNotContain(presentation.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Skills" or "Sunder.Sdk.Worker");
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Skills.Runtime.SkillAppRuntimeGateway"));
            foreach (var type in new[] { "Services.SkillStore", "Services.SkillImportService", "Services.SkillsFeature", "Runtime.SkillRuntimeHandler" })
                Assert.Null(presentation.GetType("Sunder.Package.Agent.Skills." + type));
            using var package = RegressionTestPackageScope.Create();
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.skills");
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Skills.Runtime.SkillsWorkerComposition"));
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Skills.Runtime.SkillsWorkerBindings"));
            var (options, workerLifetime, runtimeServices) = CreateGeneratedWorker(runtime, package.Context, rpc);
            await using var owned = workerLifetime;
            Assert.Empty(Directory.GetFiles(package.RootPath, "*", SearchOption.AllDirectories));
            var storeType = runtime.GetType("Sunder.Package.Agent.Skills.Services.SkillStore", true)!;
            var store = runtimeServices.GetRequiredService(storeType);
            await options.OnActivated!(null!, CancellationToken.None);
            Assert.Null(runtimeServices.GetService<AgentRpcCatalog>());
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Skills.Runtime.LegacySkillsRuntimeBindings"));
            var registrations = options.Providers.ToDictionary(value => value.ProviderId);
            Assert.Equal(new[] { "skills.management", "skills.prompt.context", "skills.selectable.capabilities", "skills.stack", "skills.tools" }, registrations.Keys.Order(StringComparer.Ordinal));
            SelectableHandlerProbe? selectableProbe = null;
            foreach (var provider in options.Providers)
            {
                var descriptor = provider.ProviderId == "skills.management" ? RegressionTestRpcDescriptors.Get("sunder.package.agent.skills.owner")
                    : provider.ProviderId == "skills.stack" ? SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(
                        BuilderSourceMigrationTests.FindRepository(), "../sunder-core/languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/Contracts/sunder.stack.contributor.rpc.json")))
                    : RegressionTestRpcDescriptors.Get(provider.ContractId);
                ISunderRpcServiceHandler handler = provider.ProviderId == "skills.selectable.capabilities"
                    ? selectableProbe = new(provider.Handler) : provider.Handler;
                state.Add("sunder.package.agent.skills", descriptor, handler, handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            var agentClient = CreateAgentSelectableClient(agentOwner, new RegressionTestRpcClient(state, "sunder.package.agent"),
                Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.SelectableCapabilityProvider)).Providers));
            await using var agentClientLifetime = (IAsyncDisposable)agentClient;
            var agentChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ((IAgentProfileSelectableCapabilityChangeNotifier)agentClient).SelectableCapabilitiesChanged += () => agentChanged.TrySetResult();
            await selectableProbe!.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(await agentClient.ListCapabilitiesAsync(new(null)));
            var appRoot = Directory.CreateDirectory(Path.Combine(package.RootPath, "app-workspace")).FullName;
            var context = new TestPackageContext("sunder.package.agent.skills", appRoot, storage: new TestPackageStorageContext(package.Context.Storage.Files,
                package.Context.Storage.State, new TestPackageRoleLocalWorkspace(appRoot)));
            await using var appServices = CreateGeneratedApp(presentation, context, rpc);
            var gateway = appServices.GetRequiredService(presentation.GetType("Sunder.Package.Agent.Skills.Runtime.SkillAppRuntimeGateway", true)!);
            var source = Directory.CreateDirectory(Path.Combine(package.RootPath, "local-source")).FullName;
            File.WriteAllText(Path.Combine(source, "SKILL.md"), "---\nname: Artifact Skill\ndescription: Artifact fixture\n---\nRead the local instructions.");
            File.WriteAllText(Path.Combine(source, "reference.txt"), "A local reference.");
            var imported = Assert.Single(((IEnumerable)(await Call("ImportLocalAsync", source, CancellationToken.None))!).Cast<object>());
            Assert.Same(presentation, imported.GetType().Assembly);
            var skillId = (string)imported.GetType().GetProperty("SkillId")!.GetValue(imported)!;
            await agentChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(skillId, Assert.Single(await agentClient.ListCapabilitiesAsync(new(null))).CapabilityId);
            Assert.Null(imported.GetType().GetProperty("SourceUri")!.GetValue(imported));
            Assert.Single(((IEnumerable)(await Call("ListAsync", CancellationToken.None))!).Cast<object>());
            var profile = new AgentProfileRecord("profile.skills", "Skills", null, null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                SelectableCapabilityAssignments: [new("skill", skillId, "sunder.package.agent.skills")]);
            var runtimeCatalog = new MemoryConsumerRuntime(profile);
            state.Add("sunder.package.agent", RegressionTestRpcDescriptors.Get(AgentRpcContractIds.RuntimeCatalog), runtimeCatalog,
                new Rpc.Public.RuntimeCatalogService(runtimeCatalog).CreateSunderRpcProviderRegistration().Handler, Noop.Instance, "agent.runtime.catalog", "2.0.0");
            var target = new SkillsConsumerHandler();
            state.Add("example.skills.executor", RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ExecutionTarget), target, target, Noop.Instance, "skills.execution.target", "2.0.0");
            var targetHandle = ToProviderHandle(Assert.Single(state.Discover(AgentRpcContractIds.ExecutionTarget).Providers));
            var now = DateTimeOffset.UtcNow;
            var workspace = new AgentWorkspaceRecord("workspace.skills", "Skills", null, now, now);
            var binding = new AgentWorkspaceBindingRecord("binding.skills", workspace.WorkspaceId, AgentRpcContractIds.ExecutionTarget, "test-target", "execution", true, 0, now, now);

            var listed = await Rpc("skills.tools", "tool-source", "list-tools", new { context = new { profile } });
            Assert.Equal(new[] { "skill", "skill_resource" }, listed.EnumerateArray().Select(value => value.GetProperty("toolId").GetString()));
            var ready = await Rpc("skills.tools", "tool-source", "get-readiness", new { toolId = "skill", context = new { context = new { profile } } });
            Assert.Equal("ready", ready.GetProperty("status").GetString());
            var execution = new { executionTarget = targetHandle, approvedResourceCapabilities = Array.Empty<string>(), context = new { allowOutsideConfiguredScope = false,
                approvedResourceClaims = Array.Empty<object>(), approvedResourceReferences = Array.Empty<string>(), profileId = profile.ProfileId,
                workspace, executionBinding = binding, transcriptEpoch = 0 } };
            var request = new { context = execution, request = new { toolId = "skill", argumentsJson = JsonSerializer.Serialize(new { name = skillId }) } };
            Assert.Equal(JsonValueKind.Null, (await Rpc("skills.tools", "tool-source", "preflight", request)).ValueKind);
            Assert.Equal(JsonValueKind.Null, (await Rpc("skills.tools", "tool-source", "permission", request)).ValueKind);
            var executed = await Rpc("skills.tools", "tool-source", "execute", request);
            Assert.False(executed.GetProperty("isError").GetBoolean());
            Assert.Contains("Read the local instructions.", executed.GetProperty("content").GetString()!, StringComparison.Ordinal);
            Assert.Contains("/executor/" + skillId, executed.GetProperty("content").GetString()!, StringComparison.Ordinal);
            var resource = await Rpc("skills.tools", "tool-source", "execute", new { context = execution,
                request = new { toolId = "skill_resource", argumentsJson = JsonSerializer.Serialize(new { skill = skillId, path = "reference.txt" }) } });
            Assert.Contains("A local reference.", resource.GetProperty("content").GetString()!, StringComparison.Ordinal);
            var prompt = await Rpc("skills.prompt.context", "prompt-context-contributor", "contribute", new
            {
                session = new { sessionId = runtimeCatalog.Session.SessionId, profileId = profile.ProfileId, profileDisplayName = profile.DisplayName, sessionTitle = "Skills", sessionState = "active" },
                run = new { runId = Guid.NewGuid(), revision = 1, status = "running", isInterrupted = false, startedAtUtc = now },
                turn = new { userMessage = "Use skills" }, turns = Array.Empty<object>(), recentLiveBufferTurns = Array.Empty<object>(),
                workspace, executionBinding = binding, executionTarget = targetHandle,
                contextPlan = new { intent = "GeneralFact", queryText = "skills", maxEntryCount = 2, maxChars = 1000, shouldContribute = true }, availableTools = Array.Empty<object>(), transcriptEpoch = 0,
            });
            Assert.Contains("Artifact Skill", prompt.GetProperty("blocks")[0].GetProperty("content").GetString()!, StringComparison.Ordinal);
            Assert.Contains("/executor/" + skillId, prompt.GetProperty("blocks")[0].GetProperty("content").GetString()!, StringComparison.Ordinal);
            var saved = JsonSerializer.SerializeToNode(imported, imported.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            saved["sourceKind"] = "github";
            saved["sourceUri"] = "https://github.com/example/skills/tree/main/artifact";
            var save = storeType.GetMethod("SaveSkill")!;
            save.Invoke(store, [saved.Deserialize(save.GetParameters()[0].ParameterType, new JsonSerializerOptions(JsonSerializerDefaults.Web))]);
            var items = await Rpc("skills.stack", "stack-contributor", "list-export-items", new { packageId = "sunder.package.agent.skills" });
            Assert.Equal(skillId, Assert.Single(items.GetProperty("items").EnumerateArray()).GetProperty("itemId").GetString());
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call("DeleteAsync", skillId, cancellation.Token));
                Assert.Single(((IEnumerable)(await Call("ListAsync", CancellationToken.None))!).Cast<object>());
            }
            var endpoint = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.SelectableCapabilityProvider)).Providers).Endpoint;
            using var lifetime = new CancellationTokenSource();
            await using var subscription = rpc.SubscribeAsync(endpoint, "selectable-capability-provider", "watch", JsonSerializer.SerializeToElement(new { }), cancellationToken: lifetime.Token).GetAsyncEnumerator();
            var next = subscription.MoveNextAsync().AsTask();
            agentChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await Call("DeleteAsync", skillId, CancellationToken.None);
            await agentChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(await agentClient.ListCapabilitiesAsync(new(null)));
            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(subscription.Current.GetProperty("revision").GetInt64() > 0);
            lifetime.Cancel();
            Assert.Empty(((IEnumerable)(await Call("ListAsync", CancellationToken.None))!).Cast<object>());
            await ((IAsyncDisposable)gateway).DisposeAsync();
            await agentClientLifetime.DisposeAsync();

            async Task<object?> Call(string method, params object?[] args)
            {
                var operation = (Task)gateway.GetType().GetMethod(method)!.Invoke(gateway, args)!;
                await operation;
                return operation.GetType().GetProperty("Result")?.GetValue(operation);
            }
            async Task<JsonElement> Rpc(string id, string serviceId, string method, object value)
            {
                var provider = registrations[id];
                var selected = Assert.Single((await rpc.DiscoverAsync(provider.ContractId)).Providers, value => value.ProviderId == id);
                return await rpc.InvokeAsync(selected.Endpoint, serviceId, method, JsonSerializer.SerializeToElement(value, MemoryProviderJson));
            }
        }
        finally { load.Unload(); }
    }
}
