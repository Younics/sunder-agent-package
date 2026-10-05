using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyFilesBehavior(string worker, string localWorker)
    {
        var load = new AssemblyLoadContext("files-generated-local-execution", isCollectible: true);
        try
        {
            using var workerBytes = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "worker"), "Sunder.Package.Agent.Tools.Files.dll"));
            var runtime = load.LoadFromStream(workerBytes);
            AssertExecutionToolSemanticOwnership(runtime);
            using var localBytes = File.OpenRead(Path.Combine(SingleProjectRoleOutput(localWorker, "worker"), "Sunder.Package.Agent.Execution.Local.dll"));
            var local = load.LoadFromStream(localBytes);
            using var package = RegressionTestPackageScope.Create();
            var root = OperatingSystem.IsMacOS() && package.RootPath.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + package.RootPath : package.RootPath;
            var context = new TestPackageContext("sunder.package.agent.tools.files", root, version: "2.0.0", storage: package.Context.Storage);
            var localContext = new TestPackageContext("sunder.package.agent.execution.local", root, version: "2.0.0", storage: package.Context.Storage);
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var (localOptions, localLifetime, _) = CreateGeneratedWorker(local, localContext, new RegressionTestRpcClient(state, localContext.PackageId));
            await using var ownedLocal = localLifetime;
            var targetRegistration = Assert.Single(localOptions.Providers, provider => provider.ProviderId == "local.execution.target");
            var target = new FilesTargetProbe(targetRegistration.Handler);
            var descriptor = RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ExecutionTarget);
            state.Add(localContext.PackageId, descriptor, target, target, Noop.Instance, targetRegistration.ProviderId, "2.0.0");
            var targetHandle = ToProviderHandle(Assert.Single(state.Discover(descriptor.ContractId).Providers));
            var observer = new FilesRpcObserver(new RegressionTestRpcClient(state, context.PackageId));
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Tools.Files.FilesWorkerComposition"));
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Tools.Files.FilesWorkerBindings"));
            var (options, lifetime, services) = CreateGeneratedWorker(runtime, context, observer);
            await using var owned = lifetime;
            Assert.Null(options.Composition);
            Assert.Null(services.GetService(typeof(AgentRpcCatalog)));
            var registrations = options.Providers.ToDictionary(provider => provider.ProviderId);
            Assert.Equal(new[] { "files.permissions", "files.prompt.context", "files.session.cleaner", "files.tools" }, registrations.Keys.Order(StringComparer.Ordinal));
            foreach (var provider in options.Providers)
            {
                var expected = RegressionTestRpcDescriptors.Get(provider.ContractId);
                Assert.Equal(expected.Sha256, provider.DescriptorSha256); Assert.Equal(expected.AbiSha256, provider.AbiSha256);
                state.Add(context.PackageId, expected, provider.Handler, provider.Handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            var host = new RegressionTestRpcClient(state, "sunder.package.agent");
            var workspaceRoot = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var outsideRoot = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            var now = DateTimeOffset.UtcNow;
            var workspace = new AgentWorkspaceRecord("workspace.files", "Files artifact", null, now, now,
                [new("path.files", "workspace.files", workspaceRoot, true, 0, now, now)]);
            var binding = new AgentWorkspaceBindingRecord("binding.files", workspace.WorkspaceId, AgentRpcContractIds.ExecutionTarget, "local", "primary-execution-target", true, 0, now, now);
            var sessionId = Guid.NewGuid(); var runId = Guid.NewGuid();
            var execution = new AgentToolExecutionContext(sessionId, "profile.files", workspace, binding, RunId: runId, RunRevision: 1, ToolCallId: "call.files") { TranscriptEpoch = 1 };
            var operation = new AgentResourceOperationContext(runId, 1, "call.files", "files.mutate", 0, "workspace-generation", "binding-generation",
                context.PackageId, localContext.PackageId, Guid.NewGuid().ToString("N"), 1, true);
            execution = execution with { ResourceOperation = operation };
            var generation = (await host.InvokeAsync(new(targetHandle.EndpointReference), "execution-target", "configuration-generation",
                ExecutionWireContext(new(sessionId, execution.ProfileId, workspace, binding)))).GetString();
            execution = execution with { ExecutionTargetConfigurationGeneration = generation };
            var claimRoot = context.Storage.RoleLocalWorkspace.GetLocalPath("files/scoped-instruction-claims");
            Assert.False(Directory.Exists(claimRoot));
            Assert.Equal("failed", (await Ready()).GetProperty("status").GetString());
            Assert.Empty(observer.Scopes); Assert.Equal(0, observer.ActiveWatches);
            await options.OnActivated!(null!, default); Assert.True(Directory.Exists(claimRoot));
            Assert.Equal("ready", (await Ready()).GetProperty("status").GetString());
            Assert.Equal(3, (await Rpc("files.permissions", "describe", new { })).GetProperty("actions").GetArrayLength());
            var tools = await Rpc("files.tools", "list-tools", ToolSourceContext(new(sessionId, null, workspace, binding), targetHandle, generation));
            Assert.Equal(new[] { "apply_patch", "edit", "glob", "grep", "read", "write" }, tools.EnumerateArray().Select(value => value.GetProperty("toolId").GetString()).Order(StringComparer.Ordinal));
            var file = Path.Combine(workspaceRoot, "file.txt"); var instructions = Path.Combine(workspaceRoot, "AGENTS.md");
            await File.WriteAllTextAsync(file, "first\nneedle\nthird"); await File.WriteAllTextAsync(instructions, "Use fixture policy.");
            var withheld = await Tool("execute", "read", new { path = "file.txt" });
            Assert.Equal("files-prompt-context-refresh-required", withheld.ErrorCode); Assert.DoesNotContain("needle", withheld.Content ?? "", StringComparison.Ordinal);
            var receipt = await RefreshPrompt();
            var read = await Tool("execute", "read", new { path = "file.txt", offset = 2, limit = 1 });
            Assert.False(read.IsError, read.Content); Assert.Equal("2: needle", read.Content);
            Assert.False((await Tool("execute", "grep", new { pattern = "needle", path = "." })).IsError);
            Assert.False((await Tool("execute", "glob", new { pattern = "*.txt", path = "." })).IsError);
            Assert.False((await Tool("execute", "write", new { path = "created.txt", content = "created" })).IsError);
            Assert.False((await Tool("execute", "edit", new { path = "created.txt", oldString = "created", newString = "edited" })).IsError);
            Assert.Equal("edited", await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "created.txt")));
            Assert.Equal("files-arguments-invalid", (await Tool("preflight", "read", new { path = "file.txt", offset = 0 })).ErrorCode);
            var presentation = await Rpc("files.tools", "presentation", new AgentToolPresentationRequest("read", "{\"path\":\"file.txt\"}", read.Summary, read.Content, null, null, false, null, read.BackendId));
            Assert.Equal(JsonValueKind.Object, presentation.ValueKind);
            await File.WriteAllTextAsync(instructions, "Changed fixture policy.");
            var deferred = await Tool("execute", "write", new { path = "created.txt", content = "must not write" });
            Assert.Equal("files-prompt-context-refresh-required", deferred.ErrorCode); Assert.Equal("edited", await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "created.txt")));
            await Assert.ThrowsAsync<SunderRpcException>(() => Rpc("files.prompt.context", "acknowledge", receipt));
            await RefreshPrompt();
            var compound = "*** Begin Patch\n*** Update File: AGENTS.md\n@@\n-Changed fixture policy.\n+New policy\n*** Update File: created.txt\n@@\n-edited\n+changed\n*** End Patch";
            Assert.Equal("files-agents-compound-patch-rejected", (await Tool("execute", "apply_patch", new { patchText = compound })).ErrorCode);
            Assert.Equal("edited", await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "created.txt")));
            await VerifyFilesMutations(target, workspaceRoot, outsideRoot, execution, Tool, Rpc, targetHandle, generation);
            await Rpc("files.session.cleaner", "delete-session", new { sessionId });
            Assert.Equal("files-prompt-context-refresh-required", (await Tool("execute", "read", new { path = "file.txt" })).ErrorCode);
            await RefreshPrompt();
            var wrongBinding = await Tool("execute", "write", new { path = "created.txt", content = "wrong target" },
                execution with { ExecutionBinding = binding with { ContributionId = "unmatched-target" } });
            Assert.Equal("files-target-required", wrongBinding.ErrorCode);
            Assert.Equal("edited", await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "created.txt")));
            foreach (var changed in new[]
            {
                targetHandle with { PackageId = "example.wrong" }, targetHandle with { PackageVersion = "2.0.1" }, targetHandle with { ProviderId = "wrong" },
                targetHandle with { ContractId = "example.wrong" }, targetHandle with { ContractVersion = "9.0.0" }, targetHandle with { DescriptorSha256 = new string('0', 64) },
                targetHandle with { AbiSha256 = new string('0', 64) }, targetHandle with { ActivationId = Guid.NewGuid() }, targetHandle with { ActivationEpoch = targetHandle.ActivationEpoch + 1 },
                targetHandle with { SessionGeneration = targetHandle.SessionGeneration + 1 }, targetHandle with { EndpointReference = "missing-endpoint" },
            }) Assert.Equal("failed", (await Ready(changed)).GetProperty("status").GetString());
            target.EmptyFacets = true; Assert.Equal("failed", (await Ready()).GetProperty("status").GetString()); target.EmptyFacets = false;
            observer.LookupFailure = SunderRpcErrorKind.PermissionDenied;
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => Ready())).Error.Kind);
            observer.LookupFailure = null;
            observer.InvalidateAfterInvoke = true;
            Assert.Equal(AgentToolResultErrorCodes.PackageUnavailable, (await Tool("execute", "read", new { path = "file.txt" })).ErrorCode);
            observer.InvalidateAfterInvoke = false; observer.Transform = null;
            target.BlockRead = new FilesCallGate();
            using (var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                var pending = Tool("execute", "read", new { path = "file.txt" }, null, cancelled.Token);
                await target.BlockRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                await target.BlockRead.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            target.BlockRead = new FilesCallGate();
            var retiringRead = Tool("execute", "read", new { path = "file.txt" });
            await target.BlockRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var retirement = state.RetireAsync(target);
            Assert.Equal(AgentToolResultErrorCodes.PackageUnavailable, (await retiringRead).ErrorCode);
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            target = new FilesTargetProbe(targetRegistration.Handler);
            state.Add(localContext.PackageId, descriptor, target, target, Noop.Instance, targetRegistration.ProviderId, "2.0.0");
            Assert.Equal("failed", (await Ready()).GetProperty("status").GetString());
            targetHandle = ToProviderHandle(Assert.Single(state.Discover(descriptor.ContractId).Providers));
            Assert.Equal("ready", (await Ready()).GetProperty("status").GetString());
            target.BlockRead = new FilesCallGate();
            var stoppingRead = Tool("execute", "read", new { path = "file.txt" });
            await target.BlockRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopped = lifetime.DisposeAsync().AsTask();
            Assert.Equal(AgentToolResultErrorCodes.PackageUnavailable, (await stoppingRead).ErrorCode);
            await stopped.WaitAsync(TimeSpan.FromSeconds(5)); await lifetime.DisposeAsync();
            Assert.Equal(0, observer.ActiveWatches); Assert.Equal(0, observer.UnscopedLookups);
            Assert.NotEmpty(observer.Scopes); Assert.All(observer.Scopes, scope => Assert.True(scope.Disposed));
            Assert.All(observer.Scopes, scope => Assert.True(scope.DeadlineUtc <= DateTimeOffset.UtcNow.AddSeconds(31)));

            async Task<JsonElement> Rpc(string id, string method, object request, CancellationToken token = default)
            {
                var provider = Assert.Single((await host.DiscoverAsync(registrations[id].ContractId, token)).Providers);
                var service = id switch { "files.tools" => "tool-source", "files.permissions" => "permission-surface", "files.prompt.context" => "prompt-context-contributor", _ => "session-cleaner" };
                await using var scope = await host.CreateCallScopeAsync(new(DeadlineUtc: DateTimeOffset.UtcNow.AddSeconds(30)), token);
                return await scope.InvokeAsync(provider.Endpoint, service, method, JsonSerializer.SerializeToElement(request, MemoryProviderJson), cancellationToken: token).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            }
            Task<JsonElement> Ready(AgentRpcProviderHandle? selected = null) => Rpc("files.tools", "get-readiness", ToolReadiness("read", new(sessionId, null, workspace, binding), selected ?? targetHandle, generation));
            async Task<AgentToolResult> Tool(string method, string tool, object args, AgentToolExecutionContext? supplied = null, CancellationToken token = default)
            {
                var current = supplied ?? execution;
                var result = await Rpc("files.tools", method, RegressionTestRpcValues.ToolInvocation(current,
                    new(tool, JsonSerializer.Serialize(args)), targetHandle), token);
                return result.Deserialize<AgentToolResult>(MemoryProviderJson)!;
            }
            async Task<AgentPromptContextReceipt> RefreshPrompt()
            {
                var value = await Rpc("files.prompt.context", "contribute", new
                {
                    session = new { sessionId, profileId = "profile.files", profileDisplayName = "Files", sessionTitle = "Files", sessionState = "active" },
                    run = new { runId, revision = 1, status = "running", isInterrupted = false, startedAtUtc = now },
                    turn = new { userMessage = "Work on files" }, turns = Array.Empty<object>(), recentLiveBufferTurns = Array.Empty<object>(),
                    workspace, executionBinding = binding, executionTarget = targetHandle,
                    contextPlan = new { intent = "GeneralFact", queryText = "files", maxEntryCount = 20, maxChars = 48000, shouldContribute = true },
                    availableTools = tools, transcriptEpoch = 1,
                });
                var blocks = value.Deserialize<AgentPromptContextContribution>(MemoryProviderJson)!.Blocks.Where(block => block.Scope is not null).ToArray();
                Assert.NotEmpty(blocks); Assert.All(blocks, block => Assert.Equal(AgentContextTrust.Untrusted, block.Trust));
                var ack = new AgentPromptContextReceipt(sessionId, runId, 1, blocks.Select((block, index) => new AgentPromptContextReceiptBlock("host.files." + index,
                    block.Scope!.ContextIdentity, block.Scope.DocumentPath, block.Scope.ContentHash)).ToArray());
                await Rpc("files.prompt.context", "acknowledge", ack); return ack;
            }
        }
        finally { load.Unload(); }
    }
}
