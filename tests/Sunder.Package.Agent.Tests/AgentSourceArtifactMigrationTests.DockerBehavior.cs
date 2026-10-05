extern alias AgentApp;

using System.Collections;
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
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias DockerWorker;
using DockerWorker::Sunder.Package.Agent.Execution.Docker;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyDockerRoles(string root, string worker)
    {
        var load = new AssemblyLoadContext("docker-role-local", isCollectible: true);
        try
        {
            using var workerStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "worker"), "Sunder.Package.Agent.Execution.Docker.dll"));
            var runtime = load.LoadFromStream(workerStream);
            await VerifyGeneratedDockerComposition(runtime);
            var owner = LoadExecutionOwner(worker, load);
            var agentApp = LoadAgentEditorOwner(worker, load);
            using var appStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "app"), "Sunder.Package.Agent.Execution.Docker.App.dll"));
            var presentation = load.LoadFromStream(appStream);
            AssertExecutionBackendOwnership(runtime, app: false);
            AssertExecutionBackendOwnership(presentation, app: true);
            Assert.NotSame(runtime.GetType("Sunder.Package.Agent.Contracts.Models.AgentShellSyntaxKinds"), presentation.GetType("Sunder.Package.Agent.Contracts.Models.AgentShellSyntaxKinds"));
            foreach (var assembly in new[] { runtime, presentation })
            {
                Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Agent.Execution.Docker.Protocol");
                foreach (var name in new[] { "DockerExecutionOperationRequest", "DockerExecutionOperationResponse", "DockerImageDefinition", "DockerExecutionPresentationDefaults" })
                    Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Execution.Docker." + name, true)!.Assembly);
            }
            Assert.DoesNotContain(presentation.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Execution.Docker" or "Sunder.Sdk.Worker");
            var runnerType = await BuildDockerBackend(root, worker, load);
            using var package = RegressionTestPackageScope.Create();
            var physicalRoot = OperatingSystem.IsMacOS() && package.RootPath.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + package.RootPath : package.RootPath;
            var context = new TestPackageContext("sunder.package.agent.execution.docker", physicalRoot, version: "2.0.0", storage: package.Context.Storage);
            var backend = new DockerArtifactBackend();
            Func<string[], int, CancellationToken, string?, Task<(int, string, bool, bool)>> run = backend.Run;
            var runner = Activator.CreateInstance(runnerType, context, run)!;
            Action<IServiceCollection> configure = services => services.AddSingleton(runtime.GetType("Sunder.Package.Agent.Execution.Docker.DockerCliRunner", true)!, runner);
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.execution.docker");
            var options = CreateControlledGeneratedWorker(runnerType.Assembly, context, rpc, configure).Options;
            await using var composition = Assert.IsType<SunderWorkerComposition>(options.Composition);
            Assert.Empty(backend.Calls);
            var migration = (ISunderWorkerLifecycleService)composition.Services.GetRequiredService(runtime.GetType("Sunder.Package.Agent.Execution.Docker.DockerPackageStorageMigration", true)!);
            await migration.PrepareCandidateAsync(); Assert.Empty(backend.Calls);
            await migration.ActivateAsync(null!);
            var registrations = options.Providers.ToDictionary(value => value.ProviderId);
            Assert.Equal(new[] { "docker.execution.target", "docker.presentation", "docker.stack", "docker.workspace.editor", "docker.workspace.path.migrator" }, registrations.Keys.Order(StringComparer.Ordinal));
            foreach (var provider in options.Providers)
            {
                var descriptor = provider.ProviderId == "docker.presentation" ? RegressionTestRpcDescriptors.Get("sunder.package.agent.execution.docker.presentation")
                    : provider.ProviderId == "docker.stack" ? SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(BuilderSourceMigrationTests.FindRepository(),
                        "../sunder-core/languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/Contracts/sunder.stack.contributor.rpc.json"))) : RegressionTestRpcDescriptors.Get(provider.ContractId);
                Assert.Equal(descriptor.Sha256, provider.DescriptorSha256); Assert.Equal(descriptor.AbiSha256, provider.AbiSha256);
                state.Add("sunder.package.agent.execution.docker", descriptor, provider.Handler, provider.Handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            await using var appServices = CreateGeneratedApp(presentation, context, rpc, new CompositionBackgroundProcessQueue());
            var client = appServices.GetRequiredService(presentation.GetType("Sunder.Package.Agent.Execution.Docker.App.DockerExecutionSourceClient", true)!);
            Assert.Equal("Sunder.Package.Agent.Execution.Docker.App.DockerExecutionSourceClient", client.GetType().FullName);
            Assert.Equal("300", (await Present(new { kind = "get-settings" })).GetProperty("timeoutSeconds").GetString());
            Assert.True((await Present(new { kind = "save-settings", timeoutSeconds = "43" })).GetProperty("success").GetBoolean());
            Assert.Equal("docker.timeout.invalid", (await Present(new { kind = "save-settings", timeoutSeconds = "0" })).GetProperty("error").GetProperty("code").GetString());
            const string image = "example.com/artifact/image:dev";
            Assert.Equal("not-pulled", Assert.Single((await Present(new { kind = "add-image", imageReference = image })).GetProperty("images").EnumerateArray()).GetProperty("status").GetString());
            Assert.Equal("ready", Assert.Single((await Present(new { kind = "refresh-image", imageReference = image })).GetProperty("images").EnumerateArray()).GetProperty("status").GetString());
            Assert.True((await Present(new { kind = "refresh-images" })).GetProperty("success").GetBoolean());
            Assert.True((await Present(new { kind = "pull-image", imageReference = image })).GetProperty("success").GetBoolean());
            Assert.True((await Present(new { kind = "test-docker" })).GetProperty("success").GetBoolean());
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Present(new { kind = "delete-image", imageReference = image }, cancelled.Token));
            }
            var viewModel = appServices.GetRequiredService(presentation.GetType("Sunder.Package.Agent.Execution.Docker.DockerExecutionSettingsViewModel", true)!);
            await ((Task)viewModel.GetType().GetMethod("InitializeAsync")!.Invoke(viewModel, [CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True((bool)viewModel.GetType().GetProperty("IsReady")!.GetValue(viewModel)!);
            var workspaceRoot = Directory.CreateDirectory(Path.Combine(physicalRoot, "workspace")).FullName;
            var now = DateTimeOffset.UtcNow;
            var workspace = new AgentWorkspaceRecord("workspace.docker", "Docker artifact", null, now, now, [new("path.docker", "workspace.docker", workspaceRoot, true, 0, now, now)]);
            var binding = new AgentWorkspaceBindingRecord("binding.docker", workspace.WorkspaceId, AgentRpcContractIds.ExecutionTarget, "docker", "primary-execution-target", true, 0, now, now);
            var editor = new AgentWorkspaceEditorContext(
                AgentRoleBoundary.As<AgentApp::Sunder.Package.Agent.Contracts.Models.AgentWorkspaceRecord>(workspace), "docker", binding.BindingId);
            var editorClient = CreateAgentWorkspaceEditor(agentApp, rpc.ForCaller("sunder.package.agent"), Assert.Single(state.Discover(AgentRpcContractIds.WorkspaceEditor).Providers));
            await using var editorLifetime = (IAsyncDisposable)editorClient;
            Assert.True(editorClient.CanEdit(editor)); Assert.Single(await editorClient.GetSectionsAsync(editor));
            Assert.True((await editorClient.SaveSectionAsync(editor,
                new("docker-execution-settings", new Dictionary<string, AgentEditorFieldValue> { ["image"] = new(image), ["shell-path"] = new("/bin/sh") }))).Success);
            var migrationContext = new AgentWorkspacePathMigrationContext(workspace, binding);
            Assert.True((await Rpc("docker.workspace.path.migrator", "workspace-path-migrator", "can-migrate", migrationContext)).GetBoolean());
            Assert.Empty((await Rpc("docker.workspace.path.migrator", "workspace-path-migrator", "list-legacy-paths", migrationContext)).EnumerateArray());
            await Rpc("docker.workspace.path.migrator", "workspace-path-migrator", "complete", migrationContext);
            Assert.Single((await Rpc("docker.stack", "stack-contributor", "list-export-items", new { packageId = context.PackageId })).GetProperty("items").EnumerateArray());
            var exported = await Rpc("docker.stack", "stack-contributor", "export", new StackExportRequest([new("docker-images")]));
            Assert.Contains(image, Assert.Single(exported.GetProperty("fragments").EnumerateArray()).GetProperty("jsonPayload").GetString()!, StringComparison.Ordinal);
            var executionContext = new AgentExecutionTargetContext(Guid.NewGuid(), "profile.docker", workspace, binding);
            var absent = await Assert.ThrowsAsync<SunderRpcException>(() => CallTarget("resolve-resources", new
            { context = ExecutionWireContext(executionContext), resources = Array.Empty<object>() }, default));
            Assert.Equal(SunderRpcErrorKind.Domain, absent.Error.Kind); Assert.Equal("agent.execution.facet-unavailable", absent.Error.Code);
            var executionRpc = new FilesRpcObserver(new RegressionTestRpcClient(state, "sunder.package.agent"));
            var executionClient = CreateAgentExecutionClient(owner, executionRpc, Assert.Single((await executionRpc.DiscoverAsync(AgentRpcContractIds.ExecutionTarget)).Providers));
            await using var executionLifetime = (IAsyncDisposable)executionClient;
            await VerifyDockerExecution(executionContext, workspaceRoot, backend,
                (method, request, token) => InvokeAgentExecution(executionClient, method, request, token));
            Assert.Equal(0, executionRpc.UnscopedLookups); Assert.All(executionRpc.Scopes, scope => Assert.True(scope.Disposed));
            Assert.Empty((await Present(new { kind = "delete-image", imageReference = image })).GetProperty("images").EnumerateArray());
            await state.RetireAsync(registrations["docker.presentation"].Handler);
            Assert.Equal("docker.presentation.unavailable", (await Assert.ThrowsAsync<SunderRpcException>(() => Present(new { kind = "get-settings" }))).Error.Code);
            // Probe transport-sized projections independently of the smaller state-value bound.
            var images = Activator.CreateInstance(runnerType.Assembly.GetType("ArtifactImageCatalog", true)!)!;
            var managementType = runtime.GetType("Sunder.Package.Agent.Execution.Docker.Rpc.ManagementService", true)!;
            var management = Activator.CreateInstance(managementType, images)!;
            var managementRegistration = Assert.IsType<SunderWorkerProviderRegistration>(managementType.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(management, null));
            state.Add("sunder.package.agent.execution.docker", RegressionTestRpcDescriptors.Get("sunder.package.agent.execution.docker.presentation"), management, managementRegistration.Handler, Noop.Instance, managementRegistration.ProviderId, "2.0.0");
            var large = await Present(new { kind = "get-settings" });
            Assert.Equal(2500, large.GetProperty("images").GetArrayLength());
            state.FailNextResponseContentRegistration(new IOException("private docker content failure"));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => Present(new { kind = "get-settings" }))).Error.Kind);
            images.GetType().GetProperty("Count")!.SetValue(images, 8500);
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => Present(new { kind = "get-settings" }))).Error.Kind);
            await state.RetireAsync(management);
            Assert.Equal("docker.presentation.unavailable", (await Assert.ThrowsAsync<SunderRpcException>(() => Present(new { kind = "get-settings" }))).Error.Code);
            await composition.DisposeAsync();
            if (!OperatingSystem.IsWindows()) Assert.True(backend.Stops > 0);

            async Task<JsonElement> Present(object request, CancellationToken token = default)
            {
                var method = client.GetType().GetMethod("ExecuteAsync")!;
                var value = JsonSerializer.SerializeToElement(request, MemoryProviderJson).Deserialize(method.GetParameters()[0].ParameterType, MemoryProviderJson)!;
                var pending = method.Invoke(client, [value, token])!;
                var task = (Task)pending.GetType().GetMethod("AsTask")!.Invoke(pending, null)!;
                await task.WaitAsync(TimeSpan.FromSeconds(15));
                var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
                Assert.Same(presentation, result.GetType().Assembly);
                return JsonSerializer.SerializeToElement(result, result.GetType(), MemoryProviderJson);
            }
            async Task<JsonElement> Rpc(string id, string service, string method, object request, CancellationToken token = default)
            {
                var selected = Assert.Single((await rpc.DiscoverAsync(registrations[id].ContractId, token)).Providers, value => value.ProviderId == id);
                return await rpc.InvokeAsync(selected.Endpoint, service, method, JsonSerializer.SerializeToElement(request, MemoryProviderJson), cancellationToken: token).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            }
            Task<JsonElement> CallTarget(string method, object request, CancellationToken token) => Rpc("docker.execution.target", "execution-target", method, request, token);
        }
        finally { load.Unload(); }
    }
}
