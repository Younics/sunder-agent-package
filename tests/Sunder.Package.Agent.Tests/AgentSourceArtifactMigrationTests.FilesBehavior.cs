using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

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
            var context = new TestPackageContext("younics.ai.agent.tools.files", root, version: "2.0.0", storage: package.Context.Storage);
            var localContext = new TestPackageContext("younics.ai.agent.execution.local", root, version: "2.0.0", storage: package.Context.Storage);
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var (localOptions, localLifetime, _) = CreateGeneratedWorker(local, localContext, new RegressionTestRpcClient(state, localContext.PackageId));
            await using var ownedLocal = localLifetime;
            var targetRegistration = Assert.Single(localOptions.Providers, provider => provider.ProviderId == "local.execution.target");
            var target = new FilesTargetProbe(targetRegistration.Handler);
            var descriptor = CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ExecutionTargets);
            state.Add(localContext.PackageId, descriptor, target, target, Noop.Instance, targetRegistration.ProviderId, "2.0.0");
            var targetClient = new RegressionTestRpcClient(state, localContext.PackageId);
            await using var targetReceiver = new GeneratedServiceTestScope(targetClient);
            var targetProvider = Assert.Single((await targetReceiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers,
                provider => provider.ProviderId == "local.execution.target");
            var targetReference = targetProvider.Reference;
            var observer = new FilesRpcObserver(new RegressionTestRpcClient(state, context.PackageId));
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Tools.Files.FilesWorkerComposition"));
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Tools.Files.FilesWorkerBindings"));
            var (options, lifetime, _) = CreateGeneratedWorker(runtime, context, observer);
            await using var owned = lifetime;
            Assert.Null(options.Composition);
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Runtime.Rpc.AgentRpcCatalog"));
            var registrations = options.Providers.ToDictionary(provider => provider.ProviderId);
            Assert.Equal(new[] { "files.permissions", "files.prompt.context", "files.session.cleaner", "files.tools" }, registrations.Keys.Order(StringComparer.Ordinal));
            foreach (var provider in options.Providers)
            {
                var expected = CurrentPublicRpcDescriptors.Get(provider.ContractId);
                Assert.Equal(expected.Sha256, provider.DescriptorSha256); Assert.Equal(expected.AbiSha256, provider.AbiSha256);
                state.Add(context.PackageId, expected, provider.Handler, provider.Handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            var host = new RegressionTestRpcClient(state, "younics.ai.agent");
            await using var toolReceiver = new GeneratedServiceTestScope(host);
            var promptClient = Assert.Single((await toolReceiver.Providers<AgentServices.IPromptContextContributorService>().ListAsync()).Providers).Service;
            var permissionClient = Assert.Single((await toolReceiver.Providers<AgentServices.IPermissionSurfaceService>().ListAsync()).Providers).Service;
            var cleanerClient = Assert.Single((await toolReceiver.Providers<AgentServices.ISessionCleanerService>().ListAsync()).Providers).Service;
            var workspaceRoot = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var outsideRoot = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            var now = DateTimeOffset.UtcNow;
            var workspace = new AgentWorkspaceRecord("workspace.files", "Files artifact", null, now, now,
                [new("path.files", "workspace.files", workspaceRoot, true, 0, now, now)]);
            var binding = new AgentWorkspaceBindingRecord("binding.files", workspace.WorkspaceId, RegressionTestBindings.ExecutionTargets, "local", "primary-execution-target", true, 0, now, now);
            var sessionId = Guid.NewGuid(); var runId = Guid.NewGuid();
            var execution = new AgentToolExecutionContext(sessionId, "profile.files", workspace, binding, RunId: runId, RunRevision: 1, ToolCallId: "call.files") { TranscriptEpoch = 1 };
            var operation = new AgentResourceOperationContext(runId, 1, "call.files", "files.mutate", 0, "workspace-generation", "binding-generation",
                context.PackageId, localContext.PackageId, Guid.NewGuid().ToString("N"), 1, true);
            execution = execution with { ResourceOperation = operation };
            var generation = await targetProvider.Service.ConfigurationGenerationAsync(new(sessionId, execution.ProfileId, workspace, binding));
            execution = execution with { ExecutionTarget = targetReference, ExecutionTargetConfigurationGeneration = generation };
            var toolSource = Assert.Single((await toolReceiver.Providers<AgentServices.IToolSourceService>().ListAsync()).Providers,
                provider => provider.ProviderId == "files.tools").Service;
            var claimRoot = context.Storage.RoleLocalWorkspace.GetLocalPath("files/scoped-instruction-claims");
            Assert.False(Directory.Exists(claimRoot));
            // Construction must be inert; the Worker host admits calls after activation.
            Assert.Empty(observer.Scopes); Assert.Equal(0, observer.ActiveWatches);
            await options.OnActivated!(null!, default); Assert.True(Directory.Exists(claimRoot));
            Assert.Equal("ready", (await Ready()).GetProperty("status").GetString());
            Assert.Equal(3, (await permissionClient.DescribeAsync()).Actions.Count);
            var tools = await toolSource.ListToolsAsync(new(sessionId, null, workspace, binding)
                { ExecutionTarget = targetReference, ExecutionTargetConfigurationGeneration = generation });
            Assert.Equal(new[] { "apply_patch", "edit", "glob", "grep", "read", "write" }, tools.Select(value => value.ToolId).Order(StringComparer.Ordinal));
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
            Assert.NotNull(await toolSource.PresentationAsync(new AgentToolPresentationRequest("read", "{\"path\":\"file.txt\"}", read.Summary, read.Content, null, null, false, null, read.BackendId)));
            await File.WriteAllTextAsync(instructions, "Changed fixture policy.");
            var deferred = await Tool("execute", "write", new { path = "created.txt", content = "must not write" });
            Assert.Equal("files-prompt-context-refresh-required", deferred.ErrorCode); Assert.Equal("edited", await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "created.txt")));
            await Assert.ThrowsAsync<SunderRpcException>(() => promptClient.AcknowledgeAsync(receipt).AsTask());
            await RefreshPrompt();
            var compound = "*** Begin Patch\n*** Update File: AGENTS.md\n@@\n-Changed fixture policy.\n+New policy\n*** Update File: created.txt\n@@\n-edited\n+changed\n*** End Patch";
            Assert.Equal("files-agents-compound-patch-rejected", (await Tool("execute", "apply_patch", new { patchText = compound })).ErrorCode);
            Assert.Equal("edited", await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "created.txt")));
            await VerifyFilesMutations(target, workspaceRoot, outsideRoot, execution, Tool, toolSource, generation);
            await cleanerClient.DeleteSessionAsync(sessionId);
            Assert.Equal("files-prompt-context-refresh-required", (await Tool("execute", "read", new { path = "file.txt" })).ErrorCode);
            await RefreshPrompt();
            var wrongBinding = await Tool("execute", "write", new { path = "created.txt", content = "wrong target" },
                execution with { ExecutionBinding = binding with { ContributionId = "unmatched-target" } });
            Assert.Equal("files-target-required", wrongBinding.ErrorCode);
            Assert.Equal("edited", await File.ReadAllTextAsync(Path.Combine(workspaceRoot, "created.txt")));
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
            targetReference = Assert.Single((await targetReceiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers,
                provider => provider.ProviderId == "local.execution.target").Reference;
            execution = execution with { ExecutionTarget = targetReference };
            Assert.Equal("ready", (await Ready()).GetProperty("status").GetString());
            target.BlockRead = new FilesCallGate();
            using var shutdownCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stoppingRead = Tool("execute", "read", new { path = "file.txt" }, null, shutdownCancellation.Token);
            Task? providersDrained = null;
            try
            {
                await target.BlockRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var releases = target.Calls.Count(call => call.Method == "release-resource-authority");
                // Model the host barrier: withdraw inbound providers and drain their
                // cancelled operations/finally blocks before OnShutdown disposes DI.
                providersDrained = Task.WhenAll(registrations.Values.Select(provider => state.RetireAsync(provider.Handler)));
                Assert.DoesNotContain(state.Discover(string.Empty).Providers, provider => provider.PackageId == context.PackageId);
                var shutdownFailure = await Assert.ThrowsAsync<SunderRpcException>(() => stoppingRead.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, shutdownFailure.Error.Kind);
                Assert.Equal("test.rpc.stale-endpoint", shutdownFailure.Error.Code);
                await target.BlockRead.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await providersDrained.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(releases + 1, target.Calls.Count(call => call.Method == "release-resource-authority"));
                Assert.False(targetReference.RetirementToken.IsCancellationRequested);
                // The recorded release completed while the foreign target and DI were
                // usable; composition disposal itself is not proof of remote revocation.
                await lifetime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                await lifetime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                await shutdownCancellation.CancelAsync();
                target.BlockRead.Release.TrySetResult();
                await Record.ExceptionAsync(() => stoppingRead.WaitAsync(TimeSpan.FromSeconds(5)));
                if (providersDrained is not null) await providersDrained.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Equal(0, observer.ActiveWatches); observer.AssertScopedOperations();
            Assert.NotEmpty(observer.Scopes); Assert.All(observer.Scopes, scope => Assert.True(scope.Disposed));
            // These generated nested calls use the regression broker's five-minute
            // default, not the retired execution wrapper's fixed thirty seconds.
            Assert.All(observer.Scopes, scope => Assert.InRange(scope.DeadlineUtc - scope.CreatedAtUtc,
                TimeSpan.Zero, TimeSpan.FromMinutes(5)));

            async Task<JsonElement> Ready() => JsonSerializer.SerializeToElement(await toolSource.GetReadinessAsync("read",
                new(sessionId, null, workspace, binding) { ExecutionTarget = targetReference, ExecutionTargetConfigurationGeneration = generation }), MemoryProviderJson);
            async Task<AgentToolResult> Tool(string method, string tool, object args, AgentToolExecutionContext? supplied = null, CancellationToken token = default)
            {
                var current = supplied ?? execution;
                var request = new AgentToolRequest(tool, JsonSerializer.Serialize(args));
                return method == "preflight"
                    ? await toolSource.PreflightAsync(current, request, token) ?? new(tool, "No preflight result.", null, null, IsError: true)
                    : await toolSource.ExecuteAsync(current, request, token);
            }
            async Task<AgentPromptContextReceipt> RefreshPrompt()
            {
                var session = new AgentSessionContextRecord(sessionId, "profile.files", "Files", "Files", AgentSessionState.Active, null);
                var run = new AgentRunContextRecord(runId, 1, AgentRunStatus.Running, false, now);
                var value = await promptClient.ContributeAsync(new AgentPromptContextRequest(
                    session, run, new(session, run, "Work on files", null), [], [],
                    new("GeneralFact", "files", MaxEntryCount: 20, MaxChars: 48000))
                {
                    Workspace = workspace, ExecutionBinding = binding, ExecutionTarget = targetReference,
                    AvailableTools = tools, TranscriptEpoch = 1,
                });
                Assert.NotNull(value);
                var blocks = value.Blocks.Where(block => block.Scope is not null).ToArray();
                Assert.NotEmpty(blocks); Assert.All(blocks, block => Assert.Equal(AgentContextTrust.Untrusted, block.Trust));
                var ack = new AgentPromptContextReceipt(sessionId, runId, 1, blocks.Select((block, index) => new AgentPromptContextReceiptBlock("host.files." + index,
                    block.Scope!.ContextIdentity, block.Scope.DocumentPath, block.Scope.ContentHash)).ToArray());
                await promptClient.AcknowledgeAsync(ack); return ack;
            }
        }
        finally { load.Unload(); }
    }
}
