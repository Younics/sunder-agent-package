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
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
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
                Assert.Null(assembly.GetType("Sunder.Package.Agent.Execution.Local.LocalExecutionOperationRequest"));
                foreach (var name in new[] { "LocalExecutionOperationResponse", "LocalShellDefinition", "LocalExecutionPresentationDefaults" })
                    Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Execution.Local." + name, true)!.Assembly);
            }
            Assert.DoesNotContain(presentation.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Execution.Local" or "Sunder.Sdk.Worker");
            using var package = RegressionTestPackageScope.Create();
            var physicalRoot = OperatingSystem.IsMacOS() && package.RootPath.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + package.RootPath : package.RootPath;
            var context = new TestPackageContext("younics.ai.agent.execution.local", physicalRoot, version: "2.0.0", storage: package.Context.Storage);
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var rpc = new RegressionTestRpcClient(state, "younics.ai.agent.execution.local");
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
            Assert.Equal(new[] { "local.execution.target", "local.presentation", "local.workspace.editor" }, registrations.Keys.Order(StringComparer.Ordinal));
            foreach (var provider in options.Providers)
            {
                var descriptor = provider.ProviderId == "local.presentation" ? CurrentPrivateRpcDescriptors.Get("younics.ai.agent.execution.local.presentation") : CurrentPublicRpcDescriptors.Get(provider.ContractId);
                Assert.Equal(descriptor.Sha256, provider.DescriptorSha256); Assert.Equal(descriptor.AbiSha256, provider.AbiSha256);
                state.Add("younics.ai.agent.execution.local", descriptor, provider.Handler, provider.Handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            await using var appServices = CreateGeneratedApp(presentation, context, rpc);
            var presentationContract = presentation.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Execution_Local_Services.IPresentationService", true)!;
            var client = appServices.GetRequiredService(presentationContract);
            Assert.Null(presentation.GetType("Sunder.Package.Agent.Execution.Local.App.LocalExecutionSourceClient"));
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
            var binding = new AgentWorkspaceBindingRecord("binding.local", workspace.WorkspaceId, RegressionTestBindings.ExecutionTargets, "local", "primary-execution-target", true, 0, now, now);
            var targetContext = new AgentExecutionTargetContext(Guid.NewGuid(), "profile.local", workspace, binding);
            var editorContext = new AgentWorkspaceEditorContext(
                AgentRoleBoundary.As<AgentApp::Sunder.Package.Agent.Contracts.Models.AgentWorkspaceRecord>(workspace), "local", binding.BindingId);
            var editorClient = CreateAgentWorkspaceEditor(agentApp, rpc.ForCaller("younics.ai.agent"), Assert.Single(state.Discover(RegressionTestBindings.WorkspaceEditors).Providers));
            await using var editorLifetime = (IAsyncDisposable)editorClient;
            Assert.True(await editorClient.CanEditAsync(editorContext)); Assert.Single(await editorClient.GetSectionsAsync(editorContext));
            Assert.True((await editorClient.SaveSectionAsync(editorContext,
                new("local-execution-settings", new Dictionary<string, AgentEditorFieldValue> { ["shell"] = new("artifact.shell") }))).Success);
            var executionRpc = new FilesRpcObserver(new RegressionTestRpcClient(state, "younics.ai.agent"));
            await using var executionReceiver = new GeneratedServiceTestScope(executionRpc);
            var executionClient = Assert.Single((await executionReceiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers,
                provider => provider.ProviderId == "local.execution.target").Service;
            await VerifyLocalExecution(targetContext, workspaceRoot, outsideRoot, shell, executionClient);
            await VerifyAgentExecutionSelection(owner, state, workspace);
            executionRpc.AssertScopedOperations(); Assert.All(executionRpc.Scopes, scope => Assert.True(scope.Disposed));
            await state.RetireAsync(registrations["local.execution.target"].Handler).WaitAsync(TimeSpan.FromSeconds(5));
            await state.RetireAsync(registrations["local.presentation"].Handler).WaitAsync(TimeSpan.FromSeconds(5));
            var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() => Present(new { kind = "get-settings" }));
            Assert.Equal("rpc.provider-not-found", unavailable.Error.Code);
            Assert.Equal(SunderRpcErrorKind.NotFound, unavailable.Error.Kind);
            await lifetime.DisposeAsync();
            Assert.Throws<ObjectDisposedException>(() => runtimeServices.GetService(targetService.GetType()));

            async Task<JsonElement> Present(object request, CancellationToken token = default)
            {
                var value = JsonSerializer.SerializeToElement(request, MemoryProviderJson);
                var method = presentationContract.GetMethod(value.GetProperty("kind").GetString() switch
                {
                    "get-settings" => "GetSettingsAsync", "save-settings" => "SaveSettingsAsync",
                    "save-shells" => "SaveShellsAsync", _ => throw new InvalidOperationException("Unknown presentation fixture operation."),
                })!;
                var arguments = method.GetParameters().Select(parameter => parameter.ParameterType == typeof(CancellationToken)
                    ? (object)token : value.GetProperty(parameter.Name!).Deserialize(parameter.ParameterType, MemoryProviderJson)).ToArray();
                var pending = method.Invoke(client, arguments)!;
                var task = (Task)pending.GetType().GetMethod("AsTask")!.Invoke(pending, null)!;
                await task.WaitAsync(TimeSpan.FromSeconds(10));
                var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
                Assert.Same(presentation, result.GetType().Assembly);
                return JsonSerializer.SerializeToElement(result, result.GetType(), MemoryProviderJson);
            }
        }
        finally { load.Unload(); }
    }

    private static async Task VerifyLocalExecution(AgentExecutionTargetContext context, string workspace, string outside, string shell,
        AgentServices.IExecutionTargetService client)
    {
        var descriptor = Json(await client.DescribeAsync());
        Assert.Equal(9, descriptor.GetProperty("facets").GetArrayLength());
        var generation = await client.ConfigurationGenerationAsync(context);
        Assert.Equal("ready", Json(await client.ReadinessAsync(context)).GetProperty("status").GetString());
        Assert.Equal(shell, (await client.ShellAsync(context)).ExecutablePath);
        Assert.Equal(workspace, (await client.ExecutionScopeAsync(context)).DefaultWorkingDirectory);
        var file = Path.Combine(workspace, "artifact.txt");
        await File.WriteAllTextAsync(file, "first\nneedle\nthird");
        var read = Json(await client.ReadFileAsync(context, new AgentFileReadRequest("artifact.txt", 2, 1)));
        Assert.Equal("needle", read.GetProperty("content").GetString()); Assert.Equal(3, read.GetProperty("totalLines").GetInt32());
        Assert.True(read.GetProperty("wasTruncated").GetBoolean());
        var search = Json(await client.StructuredFileSearchAsync(context, new AgentFileSearchRequest(".", AgentFileSearchKind.Grep, "needle")));
        Assert.Single(search.GetProperty("matches").EnumerateArray());
        var mapping = Json(await client.MapPathAsync(context, file));
        Assert.Equal(file, mapping.GetProperty("hostPath").GetString());
        await File.WriteAllTextAsync(Path.Combine(workspace, "AGENTS.md"), "Artifact instructions");
        var instructions = Json(await client.DiscoverInstructionsAsync(context, new AgentScopedInstructionDiscoveryRequest([new(workspace, true) { FollowFinalSymbolicLink = false }])));
        Assert.Contains(instructions.GetProperty("scopes").EnumerateArray().SelectMany(value => value.GetProperty("documents").EnumerateArray()), value => value.GetProperty("content").GetString() == "Artifact instructions");
        var resources = Json(await client.ResolveResourcesAsync(context, [
            new AgentExecutionResourceDescriptor("artifact", "skill", "fixture", "Artifact", workspace, "/sunder/artifact", AgentExecutionResourceAccessMode.ReadOnly) ]));
        Assert.Equal(workspace, Assert.Single(resources.EnumerateArray()).GetProperty("executionPath").GetString());
        var outsideFile = Path.Combine(outside, "outside.txt"); await File.WriteAllTextAsync(outsideFile, "original");
        var denied = Json(await client.ReadFileAsync(context, new AgentFileReadRequest(outsideFile)));
        Assert.True(denied.GetProperty("isError").GetBoolean());
        var operation = new AgentResourceOperationContext(Guid.NewGuid(), 7, "call.local", "files.mutate", 0, "workspace-generation", "binding-generation",
            "younics.ai.agent.tools.files", "younics.ai.agent.execution.local", Guid.NewGuid().ToString("N"), 1, true);
        var planning = context with { AllowOutsideConfiguredScope = true, ResourceOperation = operation, ExpectedConfigurationGeneration = generation };
        var planned = await client.ResolveFileAsync(planning, outsideFile);
        Assert.NotNull(planned.ResourceClaim); Assert.NotEmpty(planned.AuthorityReferences);
        var approved = planning with { ApprovedResourceReferences = [planned.CanonicalReference], ApprovedResourceClaims = [planned.ResourceClaim!],
            ApprovedResourceCapabilities = planned.AuthorityReferences, ResourceOperation = operation with { CanIssueOutsideAuthority = false }, CapturePostMutationResource = true };
        Assert.True((await client.ValidateResourceAuthorityAsync(approved)).IsValid);
        var written = await client.WriteFileAsync(approved, new AgentFileWriteRequest(outsideFile, "approved"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(written.IsError, written.Summary); Assert.Equal("approved", await File.ReadAllTextAsync(outsideFile));
            Assert.NotNull(written.PostMutationResource);
            var receipt = written.PostMutationResource;
            var next = approved with { ApprovedResourceReferences = [receipt.CanonicalReference], ApprovedResourceClaims = [receipt.ResourceClaim!], ApprovedResourceCapabilities = receipt.AuthorityReferences, CapturePostMutationResource = false };
            Assert.True((await client.ValidateResourceAuthorityAsync(next)).IsValid);
            var wrongActivation = next with { ResourceOperation = next.ResourceOperation! with { AuthorityActivationId = Guid.NewGuid().ToString("N") } };
            Assert.False((await client.ValidateResourceAuthorityAsync(wrongActivation)).IsValid);
            await client.ReleaseResourceAuthorityAsync(receipt.AuthorityReferences.Concat(receipt.DeleteAuthorityReferences).ToArray());
            Assert.False((await client.ValidateResourceAuthorityAsync(next)).IsValid);
        }
        else { Assert.True(written.IsError); await client.ReleaseResourceAuthorityAsync(planned.AuthorityReferences); }
        var deleted = await client.DeleteFileAsync(context, new AgentFileDeleteRequest("artifact.txt"));
        if (!OperatingSystem.IsWindows()) { Assert.False(deleted.IsError, deleted.Summary); Assert.False(File.Exists(file)); }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.WriteFileAsync(context, new AgentFileWriteRequest("cancelled.txt", "no"), cancelled.Token).AsTask());
            Assert.False(File.Exists(Path.Combine(workspace, "cancelled.txt")));
        }
        var command = OperatingSystem.IsWindows() ? "echo artifact-shell" : "printf 'artifact-shell'";
        var executed = await client.ExecuteShellAsync(context, new AgentShellCommandRequest(command));
        Assert.Contains("artifact-shell", executed.Output!, StringComparison.Ordinal);
        var arguments = OperatingSystem.IsWindows() ? new[] { "/d", "/c", "echo artifact-process" } : new[] { "-c", "printf 'artifact-process'" };
        var process = await client.ExecuteProcessAsync(context, new AgentProcessCommandRequest(shell, arguments));
        Assert.Contains("artifact-process", process.Output!, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            using var cancelled = new CancellationTokenSource();
            var marker = Path.Combine(workspace, "process-started");
            var pending = client.ExecuteProcessAsync(context,
                new AgentProcessCommandRequest(shell, ["-c", $"printf started > '{marker}'; sleep 30"]), cancelled.Token).AsTask();
            var started = Stopwatch.StartNew();
            while (!File.Exists(marker) && started.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            Assert.True(File.Exists(marker)); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        await client.AddPathAsync(context with { ExpectedConfigurationGeneration = generation }, workspace);
        Assert.Contains(await client.ListPathAsync(context), value => value == workspace);
        Assert.NotEqual(generation, await client.ConfigurationGenerationAsync(context));
        await Assert.ThrowsAsync<SunderRpcException>(() => client.ReadFileAsync(context with { ExpectedConfigurationGeneration = generation }, new AgentFileReadRequest("AGENTS.md")).AsTask());
        static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, MemoryProviderJson);
    }
}
