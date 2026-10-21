extern alias AgentApp;

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

extern alias LocalWorker;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
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
            var localContext = new TestPackageContext("younics.ai.agent.execution.local", root, version: "2.0.0", storage: package.Context.Storage);
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var (localOptions, localLifetime, _) = CreateGeneratedWorker(local, localContext, new RegressionTestRpcClient(state, localContext.PackageId));
            await using var ownedLocal = localLifetime;
            var targetRegistration = Assert.Single(localOptions.Providers, provider => provider.ProviderId == "local.execution.target");
            var target = new FilesTargetProbe(targetRegistration.Handler);
            var descriptor = CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ExecutionTargets);
            foreach (var provider in localOptions.Providers)
            {
                var contract = provider.ProviderId == "local.presentation" ? CurrentPrivateRpcDescriptors.Get("younics.ai.agent.execution.local.presentation") : CurrentPublicRpcDescriptors.Get(provider.ContractId);
                var handler = provider.ProviderId == "local.execution.target" ? (ISunderRpcServiceHandler)target : provider.Handler;
                state.Add(localContext.PackageId, contract, handler, handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            var localRpc = new RegressionTestRpcClient(state, localContext.PackageId);
            await using var targetReceiver = new GeneratedServiceTestScope(localRpc);
            var targetProvider = Assert.Single((await targetReceiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers,
                provider => provider.ProviderId == "local.execution.target");
            var targetReference = targetProvider.Reference;
            var workspaceRoot = Directory.CreateDirectory(Path.Combine(root, "workspace with spaces")).FullName;
            var outsideRoot = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            var now = DateTimeOffset.UtcNow;
            var workspace = new AgentWorkspaceRecord("workspace.shell", "Shell artifact", null, now, now,
                [new("path.shell", "workspace.shell", workspaceRoot, true, 0, now, now)]);
            var binding = new AgentWorkspaceBindingRecord("binding.shell", workspace.WorkspaceId, RegressionTestBindings.ExecutionTargets, "local", "primary-execution-target", true, 0, now, now);
            var shellPath = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("COMSPEC")! : "/bin/sh";
            var syntax = OperatingSystem.IsWindows() ? AgentShellSyntaxKinds.Cmd : AgentShellSyntaxKinds.PosixSh;
            var settings = await Local("local.presentation", "presentation", "get-settings", new { });
            var saved = await Local("local.presentation", "presentation", "save-shells", new
            {
                expectedShellCatalogRevision = settings.GetProperty("shellCatalogRevision").GetInt64(),
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
            var generation = await targetProvider.Service.ConfigurationGenerationAsync(targetContext);
            execution = execution with { ExecutionTarget = targetReference, ExecutionTargetConfigurationGeneration = generation };
            var observer = new FilesRpcObserver(new RegressionTestRpcClient(state, "younics.ai.agent.tools.shell"), "execute-shell");
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Tools.Shell.ShellWorkerComposition"));
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Tools.Shell.ShellWorkerBindings"));
            var context = new TestPackageContext("younics.ai.agent.tools.shell", root, version: "2.0.0", storage: package.Context.Storage);
            var (options, lifetime, services) = CreateGeneratedWorker(runtime, context, observer);
            await using var owned = lifetime;
            Assert.Null(options.Composition);
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Tools.Shell.Rpc.ShellExecutionBindings"));
            Assert.NotNull(services.GetService(runtime.GetType("Sunder.Package.Agent.Tools.Shell.ShellToolSource", true)!));
            var registrations = options.Providers.ToDictionary(provider => provider.ProviderId);
            Assert.Equal(new[] { "shell.permissions", "shell.prompt.context", "shell.tools" }, registrations.Keys.Order(StringComparer.Ordinal));
            foreach (var provider in options.Providers)
            {
                var expected = CurrentPublicRpcDescriptors.Get(provider.ContractId);
                Assert.Equal(expected.Sha256, provider.DescriptorSha256); Assert.Equal(expected.AbiSha256, provider.AbiSha256);
                state.Add("younics.ai.agent.tools.shell", expected, provider.Handler, provider.Handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            var host = new RegressionTestRpcClient(state, "younics.ai.agent");
            await using var toolReceiver = new GeneratedServiceTestScope(host);
            var toolSource = Assert.Single((await toolReceiver.Providers<AgentServices.IToolSourceService>().ListAsync()).Providers,
                provider => provider.ProviderId == "shell.tools").Service;
            Assert.Empty(observer.Scopes); Assert.Equal(0, observer.ActiveWatches);
            Assert.Null(options.OnActivated);
            Assert.Equal("ready", (await Ready()).GetProperty("status").GetString());
            Assert.Equal("ready", (await Ready(toolId: "bash")).GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, (await Ready(toolId: "unknown")).ValueKind);
            var tools = await toolSource.ListToolsAsync(new(sessionId, null, workspace, binding)
                { ExecutionTarget = targetReference, ExecutionTargetConfigurationGeneration = generation });
            var tool = Assert.Single(tools);
            Assert.Equal("shell", tool.ToolId); Assert.False(tool.IsReadOnly);
            Assert.Contains("bash", tool.Aliases ?? []);
            var permissionClient = Assert.Single((await toolReceiver.Providers<AgentServices.IPermissionSurfaceService>().ListAsync()).Providers).Service;
            var promptClient = Assert.Single((await toolReceiver.Providers<AgentServices.IPromptContextContributorService>().ListAsync()).Providers).Service;
            var action = Assert.Single((await permissionClient.DescribeAsync()).Actions);
            Assert.Equal("shell.execute", action.ActionId);
            Assert.Equal(AgentPermissionDecision.Ask, Assert.Single(action.Boundaries).DefaultDecision);
            var echo = OperatingSystem.IsWindows() ? "echo artifact" : "printf artifact";
            var permission = await toolSource.PermissionAsync(execution, new("shell", JsonSerializer.Serialize(new { command = echo })));
            Assert.NotNull(permission);
            Assert.Equal(AgentPermissionBoundaryIds.SelectedExecutionTarget, permission.BoundaryId); Assert.True(permission.IsMutation);
            Assert.Equal(echo, permission.Command); Assert.Empty(permission.ResourceCapabilities);
            Assert.Null(await toolSource.PreflightAsync(execution, new("shell", JsonSerializer.Serialize(new { command = echo }))));
            var session = new AgentSessionContextRecord(sessionId, "profile.shell", "Shell", "Shell", AgentSessionState.Active, null);
            var run = new AgentRunContextRecord(runId, 1, AgentRunStatus.Running, false, now);
            var prompt = await promptClient.ContributeAsync(new AgentPromptContextRequest(
                session, run, new(session, run, "Run a command", null), [], [],
                new("GeneralFact", "shell", MaxEntryCount: 2, MaxChars: 1000))
            {
                Workspace = workspace, ExecutionBinding = binding, ExecutionTarget = targetReference,
                AvailableTools = tools, TranscriptEpoch = 1,
            });
            Assert.NotNull(prompt);
            var block = Assert.Single(prompt.Blocks);
            Assert.Equal(AgentContextTrust.Untrusted, block.Trust);
            Assert.Equal((await targetProvider.Service.ShellAsync(targetContext)).Description, block.Content);
            await promptClient.AcknowledgeAsync(new AgentPromptContextReceipt(sessionId, runId, 1, []));
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
            var presentation = await toolSource.PresentationAsync(new AgentToolPresentationRequest("bash", JsonSerializer.Serialize(new { command = echo }), executed.Summary, executed.Content, null, null, false, null, executed.BackendId));
            Assert.Contains(echo, presentation!.DetailMarkdown!, StringComparison.Ordinal);
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
            observer.LookupFailure = SunderRpcErrorKind.PermissionDenied;
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => Ready())).Error.Kind); observer.LookupFailure = null;
            observer.InvalidateAfterInvoke = true;
            var onceMarker = Path.Combine(workspaceRoot, "post-dispatch-once");
            var once = OperatingSystem.IsWindows() ? $"echo x>>\"{onceMarker}\"" : $"printf x >> '{onceMarker}'";
            var beforeAmbiguous = target.Calls.Count(call => call.Method == "execute-shell");
            var uncertain = await Assert.ThrowsAsync<SunderRpcException>(() => Execute("shell", new { command = once }));
            // The nested command completed, but its generated post-Invoke identity
            // check failed. Preserve ambiguity across the process-provider boundary;
            // it is not a safe PackageUnavailable result and cannot justify replay.
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, uncertain.Error.Kind);
            Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, uncertain.Error.DispatchState);
            Assert.Equal(SunderRpcRetrySafety.Unsafe, uncertain.Error.RetrySafety);
            Assert.False(SunderRpcException.HasTrustedNoDispatchEvidence(uncertain));
            Assert.Equal(beforeAmbiguous + 1, target.Calls.Count(call => call.Method == "execute-shell"));
            Assert.Equal("x", (await File.ReadAllTextAsync(onceMarker)).TrimEnd('\r', '\n'));
            observer.InvalidateAfterInvoke = false; observer.Transform = null;
            if (!OperatingSystem.IsWindows())
                await VerifyShellProcesses(workspaceRoot, Execute, async () =>
                {
                    await state.RetireAsync(target).WaitAsync(TimeSpan.FromSeconds(10));
                    target = new FilesTargetProbe(targetRegistration.Handler);
                    state.Add(localContext.PackageId, descriptor, target, target, Noop.Instance, targetRegistration.ProviderId, "2.0.0");
                    Assert.Equal("failed", (await Ready()).GetProperty("status").GetString());
                    targetReference = Assert.Single((await targetReceiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers,
                        provider => provider.ProviderId == "local.execution.target").Reference;
                    execution = execution with { ExecutionTarget = targetReference };
                    Assert.Equal("ready", (await Ready()).GetProperty("status").GetString());
                 }, () => Task.WhenAll(registrations.Values.Select(provider => state.RetireAsync(provider.Handler))));
            await lifetime.DisposeAsync(); await lifetime.DisposeAsync();
            Assert.Equal(0, observer.ActiveWatches); observer.AssertScopedOperations();
            Assert.NotEmpty(observer.Scopes); Assert.All(observer.Scopes, scope => Assert.True(scope.Disposed));
            Assert.All(observer.Scopes, scope => Assert.InRange(scope.DeadlineUtc - scope.CreatedAtUtc,
                TimeSpan.Zero, TimeSpan.FromMinutes(5)));

            async Task<JsonElement> Local(string id, string service, string method, object request)
            {
                var provider = Assert.Single(state.Discover(localOptions.Providers.Single(value => value.ProviderId == id).ContractId).Providers);
                var json = new JsonSerializerOptions(MemoryProviderJson) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };
                return await localRpc.InvokeAsync(provider.Endpoint, service, method, JsonSerializer.SerializeToElement(request, json)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
            async Task<JsonElement> Ready(string toolId = "shell") => JsonSerializer.SerializeToElement(await toolSource.GetReadinessAsync(toolId,
                new(sessionId, null, workspace, binding) { ExecutionTarget = targetReference, ExecutionTargetConfigurationGeneration = generation }), MemoryProviderJson);
            async Task<AgentToolResult> Execute(string toolId, object args, AgentToolExecutionContext? supplied = null, CancellationToken token = default)
                => await toolSource.ExecuteAsync(supplied ?? execution, new(toolId, JsonSerializer.Serialize(args)), token);
        }
        finally { load.Unload(); }
    }
}
