extern alias AgentApp;

using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias LocalWorker;
using LocalWorker::Sunder.Package.Agent.Execution.Local;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyLocalRoles(string worker)
    {
        var load = new AssemblyLoadContext("local-role-local", isCollectible: true);
        try
        {
            using var workerStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "worker"), "Sunder.Package.Agent.Execution.Local.dll"));
            var runtime = load.LoadFromStream(workerStream);
            await BuildRoleProbes(worker, runtime, "ExecutionRoleLocalProbes.cs.txt", output: SingleProjectRoleOutput(worker, "worker"));
            var owner = LoadExecutionOwner(worker, load);
            var agentApp = LoadAgentEditorOwner(worker, load);
            using var appStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "app"), "Sunder.Package.Agent.Execution.Local.App.dll"));
            var presentation = load.LoadFromStream(appStream);
            AssertExecutionBackendOwnership(runtime, app: false);
            AssertExecutionBackendOwnership(presentation, app: true);
            Assert.NotSame(runtime.GetType("Sunder.Package.Agent.Contracts.Models.AgentShellSyntaxKinds"), presentation.GetType("Sunder.Package.Agent.Contracts.Models.AgentShellSyntaxKinds"));
            foreach (var assembly in new[] { runtime, presentation })
            {
                Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Agent.Execution.Local.Protocol");
                foreach (var name in new[] { "LocalExecutionOperationRequest", "LocalExecutionOperationResponse", "LocalShellDefinition", "LocalExecutionPresentationDefaults" })
                    Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Execution.Local." + name, true)!.Assembly);
            }
            Assert.DoesNotContain(presentation.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Execution.Local" or "Sunder.Sdk.Worker");
            using var package = RegressionTestPackageScope.Create();
            var physicalRoot = OperatingSystem.IsMacOS() && package.RootPath.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + package.RootPath : package.RootPath;
            var context = new TestPackageContext("sunder.package.agent.execution.local", physicalRoot, version: "2.0.0", storage: package.Context.Storage);
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.execution.local");
            var (options, lifetime, runtimeServices) = CreateGeneratedWorker(runtime, context, rpc);
            await using var owned = lifetime;
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Execution.Local.LocalWorkerConfiguration"));
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Execution.Local.LocalWorkerBindings"));
            Assert.Null(options.Composition); Assert.Null(options.OnActivated);
            Assert.NotNull(options.SettingsSchema);
            Assert.Empty(Directory.GetFiles(package.RootPath, "*", SearchOption.AllDirectories));
            Assert.Equal(0, state.DiscoveryCount);
            var targetService = runtimeServices.GetRequiredService(runtime.GetType("Sunder.Package.Agent.Execution.Local.LocalExecutionTarget", true)!);
            Assert.Same(options.SettingsSchema, ((ISunderWorkerSettingsSchemaProvider)targetService).SettingsSchema);
            Assert.Same(runtimeServices.GetRequiredService(runtime.GetType("Sunder.Package.Agent.Execution.Local.LocalShellCatalogService", true)!),
                targetService.GetType().GetField("_shellCatalogService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(targetService));
            Assert.Same(runtimeServices.GetRequiredService(runtime.GetType("Sunder.Package.Agent.Execution.Local.LocalExecutionWorkspaceConfigService", true)!),
                targetService.GetType().GetField("_configService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(targetService));
            var registrations = options.Providers.ToDictionary(value => value.ProviderId);
            Assert.Equal(new[] { "local.execution.target", "local.presentation", "local.workspace.editor", "local.workspace.path.migrator" }, registrations.Keys.Order(StringComparer.Ordinal));
            foreach (var provider in options.Providers)
            {
                var descriptor = provider.ProviderId == "local.presentation" ? RegressionTestRpcDescriptors.Get("sunder.package.agent.execution.local.presentation") : RegressionTestRpcDescriptors.Get(provider.ContractId);
                Assert.Equal(descriptor.Sha256, provider.DescriptorSha256); Assert.Equal(descriptor.AbiSha256, provider.AbiSha256);
                state.Add("sunder.package.agent.execution.local", descriptor, provider.Handler, provider.Handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            await using var appServices = CreateGeneratedApp(presentation, context, rpc);
            var client = appServices.GetRequiredService(presentation.GetType("Sunder.Package.Agent.Execution.Local.App.LocalExecutionSourceClient", true)!);
            Assert.Equal("Sunder.Package.Agent.Execution.Local.App.LocalExecutionSourceClient", client.GetType().FullName);
            var initial = await Present(new { kind = "get-settings" });
            Assert.Equal("300", initial.GetProperty("timeoutSeconds").GetString());
            Assert.Equal("41", (await Present(new { kind = "save-settings", timeoutSeconds = "41" })).GetProperty("timeoutSeconds").GetString());
            Assert.False((await Present(new { kind = "save-settings", timeoutSeconds = "86401" })).GetProperty("success").GetBoolean());
            var shell = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("COMSPEC")! : "/bin/sh";
            var syntax = OperatingSystem.IsWindows() ? AgentShellSyntaxKinds.Cmd : AgentShellSyntaxKinds.PosixSh;
            var revision = initial.GetProperty("shellCatalogRevision").GetInt64();
            var shellSaved = await Present(new { kind = "save-shells", expectedShellCatalogRevision = revision,
                shells = new[] { new { shellId = "artifact.shell", displayName = "Artifact shell", executablePath = shell, syntaxKind = syntax, isDetected = false } } });
            Assert.True(shellSaved.GetProperty("success").GetBoolean());
            var conflict = await Present(new { kind = "save-shells", expectedShellCatalogRevision = revision, shells = Array.Empty<object>() });
            Assert.Equal("local.shell-catalog.conflict", conflict.GetProperty("error").GetProperty("code").GetString());
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Present(new { kind = "save-settings", timeoutSeconds = "99" }, cancelled.Token));
            }
            var viewModel = appServices.GetRequiredService(presentation.GetType("Sunder.Package.Agent.Execution.Local.LocalExecutionSettingsViewModel", true)!);
            await ((Task)viewModel.GetType().GetMethod("InitializeAsync")!.Invoke(viewModel, [CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True((bool)viewModel.GetType().GetProperty("IsReady")!.GetValue(viewModel)!);
            Assert.Equal("41", viewModel.GetType().GetProperty("TimeoutSeconds")!.GetValue(viewModel));

            var workspaceRoot = Directory.CreateDirectory(Path.Combine(physicalRoot, "workspace")).FullName;
            var outsideRoot = Directory.CreateDirectory(Path.Combine(physicalRoot, "outside")).FullName;
            var now = DateTimeOffset.UtcNow;
            var workspace = new AgentWorkspaceRecord("workspace.local", "Local artifact", null, now, now,
                [new("path.local", "workspace.local", workspaceRoot, true, 0, now, now)]);
            var binding = new AgentWorkspaceBindingRecord("binding.local", workspace.WorkspaceId, AgentRpcContractIds.ExecutionTarget, "local", "primary-execution-target", true, 0, now, now);
            var targetContext = new AgentExecutionTargetContext(Guid.NewGuid(), "profile.local", workspace, binding);
            var editorContext = new AgentWorkspaceEditorContext(
                AgentRoleBoundary.As<AgentApp::Sunder.Package.Agent.Contracts.Models.AgentWorkspaceRecord>(workspace), "local", binding.BindingId);
            var editorClient = CreateAgentWorkspaceEditor(agentApp, rpc.ForCaller("sunder.package.agent"), Assert.Single(state.Discover(AgentRpcContractIds.WorkspaceEditor).Providers));
            await using var editorLifetime = (IAsyncDisposable)editorClient;
            Assert.True(editorClient.CanEdit(editorContext)); Assert.Single(await editorClient.GetSectionsAsync(editorContext));
            Assert.True((await editorClient.SaveSectionAsync(editorContext,
                new("local-execution-settings", new Dictionary<string, AgentEditorFieldValue> { ["shell"] = new("artifact.shell") }))).Success);
            var migration = new AgentWorkspacePathMigrationContext(workspace, binding);
            Assert.True((await Rpc("local.workspace.path.migrator", "workspace-path-migrator", "can-migrate", migration)).GetBoolean());
            var key = (string)runtime.GetType("Sunder.Package.Agent.Execution.Local.LocalExecutionWorkspaceConfigService", true)!
                .GetMethod("BuildKey", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [binding.BindingId])!;
            await context.Storage.State.SetValueAsync(key, JsonSerializer.Serialize(new { SelectedShellId = "artifact.shell", PathEntries = Array.Empty<string>(), AllowedRoots = new[] { workspaceRoot }, DefaultWorkingDirectory = workspaceRoot }));
            var paths = await Rpc("local.workspace.path.migrator", "workspace-path-migrator", "list-legacy-paths", migration);
            Assert.Equal(workspaceRoot, Assert.Single(paths.EnumerateArray()).GetProperty("hostPath").GetString());
            await Rpc("local.workspace.path.migrator", "workspace-path-migrator", "complete", migration);
            Assert.Empty((await Rpc("local.workspace.path.migrator", "workspace-path-migrator", "list-legacy-paths", migration)).EnumerateArray());
            var executionRpc = new FilesRpcObserver(new RegressionTestRpcClient(state, "sunder.package.agent"));
            var executionClient = CreateAgentExecutionClient(owner, executionRpc, Assert.Single((await executionRpc.DiscoverAsync(AgentRpcContractIds.ExecutionTarget)).Providers));
            await using var executionLifetime = (IAsyncDisposable)executionClient;
            await VerifyLocalExecution(targetContext, workspaceRoot, outsideRoot, shell,
                (method, request, token) => InvokeAgentExecution(executionClient, method, request, token));
            await VerifyAgentExecutionSelection(owner, state, workspace);
            Assert.Equal(0, executionRpc.UnscopedLookups); Assert.All(executionRpc.Scopes, scope => Assert.True(scope.Disposed));
            await state.RetireAsync(registrations["local.execution.target"].Handler).WaitAsync(TimeSpan.FromSeconds(5));
            await state.RetireAsync(registrations["local.presentation"].Handler).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("local.presentation.unavailable", (await Assert.ThrowsAsync<SunderRpcException>(() => Present(new { kind = "get-settings" }))).Error.Code);
            await lifetime.DisposeAsync();
            Assert.Throws<ObjectDisposedException>(() => runtimeServices.GetService(targetService.GetType()));

            async Task<JsonElement> Present(object request, CancellationToken token = default)
            {
                var method = client.GetType().GetMethod("ExecuteAsync")!;
                var value = JsonSerializer.SerializeToElement(request, MemoryProviderJson).Deserialize(method.GetParameters()[0].ParameterType, MemoryProviderJson)!;
                var pending = method.Invoke(client, [value, token])!;
                var task = (Task)pending.GetType().GetMethod("AsTask")!.Invoke(pending, null)!;
                await task.WaitAsync(TimeSpan.FromSeconds(10));
                var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
                Assert.Same(presentation, result.GetType().Assembly);
                return JsonSerializer.SerializeToElement(result, result.GetType(), MemoryProviderJson);
            }
            async Task<JsonElement> Rpc(string id, string service, string method, object request, CancellationToken token = default)
            {
                var selected = Assert.Single((await rpc.DiscoverAsync(registrations[id].ContractId, token)).Providers, value => value.ProviderId == id);
                return await rpc.InvokeAsync(selected.Endpoint, service, method, JsonSerializer.SerializeToElement(request, MemoryProviderJson), cancellationToken: token).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally { load.Unload(); }
    }

    private static async Task VerifyLocalExecution(AgentExecutionTargetContext context, string workspace, string outside, string shell,
        Func<string, object, CancellationToken, Task<JsonElement>> invoke)
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var descriptor = await Call("describe", new { });
        Assert.Equal(9, descriptor.GetProperty("facets").GetArrayLength());
        var generation = (await Call("configuration-generation", ExecutionWireContext(context))).GetString()!;
        Assert.Equal("ready", (await Call("readiness", ExecutionWireContext(context))).GetProperty("status").GetString());
        Assert.Equal(shell, (await Call("shell", ExecutionWireContext(context))).GetProperty("executablePath").GetString());
        Assert.Equal(workspace, (await Call("execution-scope", ExecutionWireContext(context))).GetProperty("defaultWorkingDirectory").GetString());
        var file = Path.Combine(workspace, "artifact.txt");
        await File.WriteAllTextAsync(file, "first\nneedle\nthird");
        var read = await Call("read-file", new { context = ExecutionWireContext(context), request = new AgentFileReadRequest("artifact.txt", 2, 1) });
        Assert.Equal("needle", read.GetProperty("content").GetString()); Assert.Equal(3, read.GetProperty("totalLines").GetInt32());
        Assert.True(read.GetProperty("wasTruncated").GetBoolean());
        var search = await Call("structured-file-search", new { context = ExecutionWireContext(context), request = new AgentFileSearchRequest(".", AgentFileSearchKind.Grep, "needle") });
        Assert.Single(search.GetProperty("matches").EnumerateArray());
        var mapping = await Call("map-path", new { context = ExecutionWireContext(context), path = file });
        Assert.Equal(file, mapping.GetProperty("hostPath").GetString());
        await File.WriteAllTextAsync(Path.Combine(workspace, "AGENTS.md"), "Artifact instructions");
        var instructions = await Call("discover-instructions", new { context = ExecutionWireContext(context), request = new AgentScopedInstructionDiscoveryRequest([new(workspace, true) { FollowFinalSymbolicLink = false }]) });
        Assert.Contains(instructions.GetProperty("scopes").EnumerateArray().SelectMany(value => value.GetProperty("documents").EnumerateArray()), value => value.GetProperty("content").GetString() == "Artifact instructions");
        var resources = await Call("resolve-resources", new { context = ExecutionWireContext(context), resources = new[] {
            new AgentExecutionResourceDescriptor("artifact", "skill", "fixture", "Artifact", workspace, "/sunder/artifact", AgentExecutionResourceAccessMode.ReadOnly) } });
        Assert.Equal(workspace, Assert.Single(resources.EnumerateArray()).GetProperty("executionPath").GetString());
        var outsideFile = Path.Combine(outside, "outside.txt"); await File.WriteAllTextAsync(outsideFile, "original");
        var denied = await Call("read-file", new { context = ExecutionWireContext(context), request = new AgentFileReadRequest(outsideFile) });
        Assert.True(denied.GetProperty("isError").GetBoolean());
        var operation = new AgentResourceOperationContext(Guid.NewGuid(), 7, "call.local", "files.mutate", 0, "workspace-generation", "binding-generation",
            "sunder.package.agent.tools.files", "sunder.package.agent.execution.local", Guid.NewGuid().ToString("N"), 1, true);
        var planning = context with { AllowOutsideConfiguredScope = true, ResourceOperation = operation, ExpectedConfigurationGeneration = generation };
        var planned = ReadExecutionResource(await Call("resolve-file", new { context = ExecutionWireContext(planning), path = outsideFile }));
        Assert.NotNull(planned.ResourceClaim); Assert.NotEmpty(planned.AuthorityReferences);
        var approved = planning with { ApprovedResourceReferences = [planned.CanonicalReference], ApprovedResourceClaims = [planned.ResourceClaim!],
            ApprovedResourceCapabilities = planned.AuthorityReferences, ResourceOperation = operation with { CanIssueOutsideAuthority = false }, CapturePostMutationResource = true };
        Assert.True((await Call("validate-resource-authority", ExecutionWireContext(approved))).GetProperty("isValid").GetBoolean());
        var written = ReadExecutionMutation(await Call("write-file", new { context = ExecutionWireContext(approved), request = new AgentFileWriteRequest(outsideFile, "approved") }));
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(written.IsError, written.Summary); Assert.Equal("approved", await File.ReadAllTextAsync(outsideFile));
            Assert.NotNull(written.PostMutationResource);
            var receipt = written.PostMutationResource;
            var next = approved with { ApprovedResourceReferences = [receipt.CanonicalReference], ApprovedResourceClaims = [receipt.ResourceClaim!], ApprovedResourceCapabilities = receipt.AuthorityReferences, CapturePostMutationResource = false };
            Assert.True((await Call("validate-resource-authority", ExecutionWireContext(next))).GetProperty("isValid").GetBoolean());
            var wrongActivation = next with { ResourceOperation = next.ResourceOperation! with { AuthorityActivationId = Guid.NewGuid().ToString("N") } };
            Assert.False((await Call("validate-resource-authority", ExecutionWireContext(wrongActivation))).GetProperty("isValid").GetBoolean());
            await Call("release-resource-authority", new { capabilities = receipt.AuthorityReferences.Concat(receipt.DeleteAuthorityReferences).ToArray() });
            Assert.False((await Call("validate-resource-authority", ExecutionWireContext(next))).GetProperty("isValid").GetBoolean());
        }
        else { Assert.True(written.IsError); await Call("release-resource-authority", new { capabilities = planned.AuthorityReferences }); }
        var deleted = ReadExecutionMutation(await Call("delete-file", new { context = ExecutionWireContext(context), request = new AgentFileDeleteRequest("artifact.txt") }));
        if (!OperatingSystem.IsWindows()) { Assert.False(deleted.IsError, deleted.Summary); Assert.False(File.Exists(file)); }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call("write-file", new { context = ExecutionWireContext(context), request = new AgentFileWriteRequest("cancelled.txt", "no") }, cancelled.Token));
            Assert.False(File.Exists(Path.Combine(workspace, "cancelled.txt")));
        }
        var command = OperatingSystem.IsWindows() ? "echo artifact-shell" : "printf 'artifact-shell'";
        var executed = await Call("execute-shell", new { context = ExecutionWireContext(context), request = new AgentShellCommandRequest(command) });
        Assert.Contains("artifact-shell", executed.GetProperty("output").GetString()!, StringComparison.Ordinal);
        var arguments = OperatingSystem.IsWindows() ? new[] { "/d", "/c", "echo artifact-process" } : new[] { "-c", "printf 'artifact-process'" };
        var process = await Call("execute-process", new { context = ExecutionWireContext(context), request = new AgentProcessCommandRequest(shell, arguments) });
        Assert.Contains("artifact-process", process.GetProperty("output").GetString()!, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            using var cancelled = new CancellationTokenSource();
            var marker = Path.Combine(workspace, "process-started");
            var pending = Call("execute-process", new { context = ExecutionWireContext(context),
                request = new AgentProcessCommandRequest(shell, ["-c", $"printf started > '{marker}'; sleep 30"]) }, cancelled.Token);
            var started = Stopwatch.StartNew();
            while (!File.Exists(marker) && started.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            Assert.True(File.Exists(marker)); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        await Call("add-path", new { context = ExecutionWireContext(context with { ExpectedConfigurationGeneration = generation }), path = workspace });
        Assert.Contains((await Call("list-path", ExecutionWireContext(context))).EnumerateArray(), value => value.GetString() == workspace);
        Assert.NotEqual(generation, (await Call("configuration-generation", ExecutionWireContext(context))).GetString());
        await Assert.ThrowsAsync<SunderRpcException>(() => Call("read-file", new { context = ExecutionWireContext(context with { ExpectedConfigurationGeneration = generation }), request = new AgentFileReadRequest("AGENTS.md") }));
        Assert.Equal(RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ExecutionTarget).Services.Single().Methods.Select(value => value.MethodId).Order(StringComparer.Ordinal), covered.Order(StringComparer.Ordinal));
        Task<JsonElement> Call(string method, object request, CancellationToken token = default) { covered.Add(method); return invoke(method, request, token); }
    }
}
