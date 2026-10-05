extern alias AgentApp;

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

extern alias LocalWorker;
using LocalWorker::Sunder.Package.Agent.Execution.Local;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyShellBehavior(string worker, string localWorker)
    {
        var load = new AssemblyLoadContext("shell-generated-local-execution", isCollectible: true);
        try
        {
            using var workerBytes = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "worker"), "Sunder.Package.Agent.Tools.Shell.dll"));
            var runtime = load.LoadFromStream(workerBytes);
            AssertExecutionToolSemanticOwnership(runtime);
            using var localBytes = File.OpenRead(Path.Combine(SingleProjectRoleOutput(localWorker, "worker"), "Sunder.Package.Agent.Execution.Local.dll"));
            var local = load.LoadFromStream(localBytes);
            using var package = RegressionTestPackageScope.Create();
            var root = OperatingSystem.IsMacOS() && package.RootPath.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + package.RootPath : package.RootPath;
            var localContext = new TestPackageContext("sunder.package.agent.execution.local", root, version: "2.0.0", storage: package.Context.Storage);
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var (localOptions, localLifetime, _) = CreateGeneratedWorker(local, localContext, new RegressionTestRpcClient(state, localContext.PackageId));
            await using var ownedLocal = localLifetime;
            var targetRegistration = Assert.Single(localOptions.Providers, provider => provider.ProviderId == "local.execution.target");
            var target = new FilesTargetProbe(targetRegistration.Handler);
            var descriptor = RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ExecutionTarget);
            foreach (var provider in localOptions.Providers)
            {
                var contract = provider.ProviderId == "local.presentation" ? RegressionTestRpcDescriptors.Get("sunder.package.agent.execution.local.presentation") : RegressionTestRpcDescriptors.Get(provider.ContractId);
                var handler = provider.ProviderId == "local.execution.target" ? (ISunderRpcServiceHandler)target : provider.Handler;
                state.Add(localContext.PackageId, contract, handler, handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            var targetHandle = ToProviderHandle(Assert.Single(state.Discover(descriptor.ContractId).Providers));
            var localRpc = new RegressionTestRpcClient(state, localContext.PackageId);
            var workspaceRoot = Directory.CreateDirectory(Path.Combine(root, "workspace with spaces")).FullName;
            var outsideRoot = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            var now = DateTimeOffset.UtcNow;
            var workspace = new AgentWorkspaceRecord("workspace.shell", "Shell artifact", null, now, now,
                [new("path.shell", "workspace.shell", workspaceRoot, true, 0, now, now)]);
            var binding = new AgentWorkspaceBindingRecord("binding.shell", workspace.WorkspaceId, AgentRpcContractIds.ExecutionTarget, "local", "primary-execution-target", true, 0, now, now);
            var shellPath = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("COMSPEC")! : "/bin/sh";
            var syntax = OperatingSystem.IsWindows() ? AgentShellSyntaxKinds.Cmd : AgentShellSyntaxKinds.PosixSh;
            var settings = await Local("local.presentation", "presentation", "execute", new { kind = "get-settings" });
            var saved = await Local("local.presentation", "presentation", "execute", new
            {
                kind = "save-shells", expectedShellCatalogRevision = settings.GetProperty("shellCatalogRevision").GetInt64(),
                shells = new[] { new { shellId = "artifact.shell", displayName = "Artifact shell", executablePath = shellPath, syntaxKind = syntax, isDetected = false } },
            });
            Assert.True(saved.GetProperty("success").GetBoolean());
            Assert.True((await Local("local.workspace.editor", "workspace-editor", "save-section", new
            {
                context = new AgentWorkspaceEditorContext(
                    AgentRoleBoundary.As<AgentApp::Sunder.Package.Agent.Contracts.Models.AgentWorkspaceRecord>(workspace), "local", binding.BindingId),
                request = new AgentEditorSaveRequest("local-execution-settings", new Dictionary<string, AgentEditorFieldValue> { ["shell"] = new("artifact.shell") }),
            })).GetProperty("success").GetBoolean());
            var sessionId = Guid.NewGuid(); var runId = Guid.NewGuid();
            var execution = new AgentToolExecutionContext(sessionId, "profile.shell", workspace, binding, RunId: runId, RunRevision: 1, ToolCallId: "call.shell") { TranscriptEpoch = 1 };
            var targetContext = new AgentExecutionTargetContext(sessionId, execution.ProfileId, workspace, binding);
            var generation = (await Local("local.execution.target", "execution-target", "configuration-generation", ExecutionWireContext(targetContext))).GetString();
            execution = execution with { ExecutionTargetConfigurationGeneration = generation };
            var observer = new FilesRpcObserver(new RegressionTestRpcClient(state, "sunder.package.agent.tools.shell"), "execute-shell");
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Tools.Shell.ShellWorkerComposition"));
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Tools.Shell.ShellWorkerBindings"));
            var context = new TestPackageContext("sunder.package.agent.tools.shell", root, version: "2.0.0", storage: package.Context.Storage);
            var (options, lifetime, services) = CreateGeneratedWorker(runtime, context, observer);
            await using var owned = lifetime;
            Assert.Null(options.Composition);
            Assert.NotNull(services.GetService(runtime.GetType("Sunder.Package.Agent.Tools.Shell.Rpc.ShellExecutionBindings", true)!));
            var registrations = options.Providers.ToDictionary(provider => provider.ProviderId);
            Assert.Equal(new[] { "shell.permissions", "shell.prompt.context", "shell.tools" }, registrations.Keys.Order(StringComparer.Ordinal));
            foreach (var provider in options.Providers)
            {
                var expected = RegressionTestRpcDescriptors.Get(provider.ContractId);
                Assert.Equal(expected.Sha256, provider.DescriptorSha256); Assert.Equal(expected.AbiSha256, provider.AbiSha256);
                state.Add("sunder.package.agent.tools.shell", expected, provider.Handler, provider.Handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            var host = new RegressionTestRpcClient(state, "sunder.package.agent");
            Assert.Equal("failed", (await Ready()).GetProperty("status").GetString());
            Assert.Empty(observer.Scopes); Assert.Equal(0, observer.ActiveWatches);
            await options.OnActivated!(null!, default);
            Assert.Equal("ready", (await Ready()).GetProperty("status").GetString());
            Assert.Equal("ready", (await Ready(toolId: "bash")).GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, (await Ready(toolId: "unknown")).ValueKind);
            var tools = await Rpc("shell.tools", "list-tools", ToolSourceContext(new(sessionId, null, workspace, binding), targetHandle, generation));
            var tool = Assert.Single(tools.EnumerateArray());
            Assert.Equal("shell", tool.GetProperty("toolId").GetString()); Assert.False(tool.GetProperty("isReadOnly").GetBoolean());
            Assert.Contains(tool.GetProperty("aliases").EnumerateArray(), alias => alias.GetString() == "bash");
            var surface = await Rpc("shell.permissions", "describe", new { });
            var action = Assert.Single(surface.GetProperty("actions").EnumerateArray());
            Assert.Equal("shell.execute", action.GetProperty("actionId").GetString());
            Assert.Equal("ask", Assert.Single(action.GetProperty("boundaries").EnumerateArray()).GetProperty("defaultDecision").GetString());
            var echo = OperatingSystem.IsWindows() ? "echo artifact" : "printf artifact";
            var permission = ReadToolPermission(await Rpc("shell.tools", "permission", Request("shell", new { command = echo })));
            Assert.Equal(AgentPermissionBoundaryIds.SelectedExecutionTarget, permission.BoundaryId); Assert.True(permission.IsMutation);
            Assert.Equal(echo, permission.Command); Assert.Empty(permission.ResourceCapabilities);
            Assert.Equal(JsonValueKind.Null, (await Rpc("shell.tools", "preflight", Request("shell", new { command = echo }))).ValueKind);
            var prompt = await Rpc("shell.prompt.context", "contribute", new
            {
                session = new { sessionId, profileId = "profile.shell", profileDisplayName = "Shell", sessionTitle = "Shell", sessionState = "active" },
                run = new { runId, revision = 1, status = "running", isInterrupted = false, startedAtUtc = now },
                turn = new { userMessage = "Run a command" }, turns = Array.Empty<object>(), recentLiveBufferTurns = Array.Empty<object>(),
                workspace, executionBinding = binding, executionTarget = targetHandle,
                contextPlan = new { intent = "GeneralFact", queryText = "shell", maxEntryCount = 2, maxChars = 1000, shouldContribute = true },
                availableTools = tools, transcriptEpoch = 1,
            });
            var block = Assert.Single(prompt.Deserialize<AgentPromptContextContribution>(MemoryProviderJson)!.Blocks);
            Assert.Equal(AgentContextTrust.Untrusted, block.Trust);
            Assert.Equal((await Local("local.execution.target", "execution-target", "shell", ExecutionWireContext(targetContext))).GetProperty("description").GetString(), block.Content);
            await Rpc("shell.prompt.context", "acknowledge", new AgentPromptContextReceipt(sessionId, runId, 1, []));
            foreach (var args in new object[] { new { command = echo, timeoutSeconds = 0 }, new { command = echo, timeoutSeconds = 86401 }, new { workingDirectory = workspaceRoot }, new { command = echo, timeoutSeconds = "invalid" } })
            {
                var before = target.Calls.Count(call => call.Method == "execute-shell");
                var result = await Execute("shell", args);
                Assert.Equal("shell-arguments-invalid", result.ErrorCode);
                Assert.Equal(before, target.Calls.Count(call => call.Method == "execute-shell"));
                Assert.All(observer.Scopes, scope => Assert.True(scope.Disposed));
            }
            var executed = await Execute("bash", new { command = echo, workingDirectory = workspaceRoot, timeoutSeconds = "5" });
            Assert.False(executed.IsError, executed.Content); Assert.Equal("artifact", executed.Content!.Trim()); Assert.True(executed.RequiresPromptContextRefresh);
            Assert.Equal(5, target.Calls.Last(call => call.Method == "execute-shell").Request.GetProperty("request").GetProperty("timeoutSeconds").GetInt32());
            var failed = await Execute("shell", new { command = "exit 7" });
            Assert.True(failed.IsError); Assert.Equal(AgentToolResultErrorCodes.ShellNonZeroExit, failed.ErrorCode); Assert.True(failed.RequiresPromptContextRefresh);
            var presentation = await Rpc("shell.tools", "presentation", new AgentToolPresentationRequest("bash", JsonSerializer.Serialize(new { command = echo }), executed.Summary, executed.Content, null, null, false, null, executed.BackendId));
            Assert.Contains(echo, presentation.GetProperty("detailMarkdown").GetString()!, StringComparison.Ordinal);
            var marker = Path.Combine(workspaceRoot, "must-not-run");
            var mark = OperatingSystem.IsWindows() ? $"echo unexpected > \"{marker}\"" : $"printf unexpected > '{marker}'";
            var stale = await Assert.ThrowsAsync<SunderRpcException>(() => Execute("shell", new { command = mark }, execution with { ExecutionTargetConfigurationGeneration = "changed-generation" }));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, stale.Error.Kind); Assert.False(File.Exists(marker));
            var wrong = await Execute("shell", new { command = mark }, execution with { ExecutionBinding = binding with { ContributionId = "different-target" } });
            Assert.Equal("shell-target-required", wrong.ErrorCode); Assert.False(File.Exists(marker));
            var pwd = OperatingSystem.IsWindows() ? "cd" : "pwd";
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => Execute("shell", new { command = pwd, workingDirectory = outsideRoot }))).Error.Kind);
            var outside = await Execute("shell", new { command = pwd, workingDirectory = outsideRoot }, execution with { AllowOutsideConfiguredScope = true });
            Assert.False(outside.IsError, outside.Content); Assert.Contains(outsideRoot, outside.Content!, StringComparison.OrdinalIgnoreCase);
            target.DisableShell = true; Assert.Equal("failed", (await Ready()).GetProperty("status").GetString()); target.DisableShell = false;
            foreach (var changed in new[]
            {
                targetHandle with { PackageId = "example.wrong" }, targetHandle with { PackageVersion = "2.0.1" }, targetHandle with { ProviderId = "wrong" },
                targetHandle with { ContractId = "example.wrong" }, targetHandle with { ContractVersion = "9.0.0" }, targetHandle with { DescriptorSha256 = new string('0', 64) },
                targetHandle with { AbiSha256 = new string('0', 64) }, targetHandle with { ActivationId = Guid.NewGuid() }, targetHandle with { ActivationEpoch = targetHandle.ActivationEpoch + 1 },
                targetHandle with { SessionGeneration = targetHandle.SessionGeneration + 1 }, targetHandle with { EndpointReference = "missing-endpoint" },
            }) Assert.Equal("failed", (await Ready(changed)).GetProperty("status").GetString());
            observer.LookupFailure = SunderRpcErrorKind.PermissionDenied;
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => Ready())).Error.Kind); observer.LookupFailure = null;
            observer.InvalidateAfterInvoke = true;
            Assert.Equal(AgentToolResultErrorCodes.PackageUnavailable, (await Execute("shell", new { command = echo })).ErrorCode);
            observer.InvalidateAfterInvoke = false; observer.Transform = null;
            if (!OperatingSystem.IsWindows())
                await VerifyShellProcesses(workspaceRoot, Execute, async () =>
                {
                    await state.RetireAsync(target).WaitAsync(TimeSpan.FromSeconds(10));
                    target = new FilesTargetProbe(targetRegistration.Handler);
                    state.Add(localContext.PackageId, descriptor, target, target, Noop.Instance, targetRegistration.ProviderId, "2.0.0");
                    Assert.Equal("failed", (await Ready()).GetProperty("status").GetString());
                    targetHandle = ToProviderHandle(Assert.Single(state.Discover(descriptor.ContractId).Providers));
                    Assert.Equal("ready", (await Ready()).GetProperty("status").GetString());
                }, lifetime);
            await lifetime.DisposeAsync(); await lifetime.DisposeAsync();
            Assert.Equal(0, observer.ActiveWatches); Assert.Equal(0, observer.UnscopedLookups);
            Assert.NotEmpty(observer.Scopes); Assert.All(observer.Scopes, scope => Assert.True(scope.Disposed));
            Assert.All(observer.Scopes, scope => Assert.True(scope.DeadlineUtc <= DateTimeOffset.UtcNow.AddSeconds(31)));

            async Task<JsonElement> Local(string id, string service, string method, object request)
            {
                var provider = Assert.Single(state.Discover(localOptions.Providers.Single(value => value.ProviderId == id).ContractId).Providers);
                return await localRpc.InvokeAsync(provider.Endpoint, service, method, JsonSerializer.SerializeToElement(request, MemoryProviderJson)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
            async Task<JsonElement> Rpc(string id, string method, object request, CancellationToken token = default)
            {
                var provider = Assert.Single((await host.DiscoverAsync(registrations[id].ContractId, token)).Providers);
                var service = id switch { "shell.tools" => "tool-source", "shell.permissions" => "permission-surface", _ => "prompt-context-contributor" };
                await using var scope = await host.CreateCallScopeAsync(new(DeadlineUtc: DateTimeOffset.UtcNow.AddSeconds(30)), token);
                return await scope.InvokeAsync(provider.Endpoint, service, method, JsonSerializer.SerializeToElement(request, MemoryProviderJson), cancellationToken: token).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            }
            Task<JsonElement> Ready(AgentRpcProviderHandle? selected = null, string toolId = "shell") => Rpc("shell.tools", "get-readiness", ToolReadiness(toolId, new(sessionId, null, workspace, binding), selected ?? targetHandle, generation));
            JsonElement Request(string toolId, object args, AgentToolExecutionContext? supplied = null)
            {
                var current = supplied ?? execution;
                return RegressionTestRpcValues.ToolInvocation(current, new(toolId, JsonSerializer.Serialize(args)), targetHandle);
            }
            async Task<AgentToolResult> Execute(string toolId, object args, AgentToolExecutionContext? supplied = null, CancellationToken token = default)
                => (await Rpc("shell.tools", "execute", Request(toolId, args, supplied), token)).Deserialize<AgentToolResult>(MemoryProviderJson)!;
        }
        finally { load.Unload(); }
    }
}
