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
using Sunder.Sdk.Services;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias DockerWorker;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using AppServices = AgentApp::Sunder.Generated.Packages.Younics_Ai_Agent_Services;
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
            using var appStream = File.OpenRead(Path.Combine(SingleProjectRoleOutput(worker, "app"), "Sunder.Package.Agent.Execution.Docker.App.dll"));
            var presentation = load.LoadFromStream(appStream);
            AssertExecutionBackendOwnership(runtime, app: false);
            AssertExecutionBackendOwnership(presentation, app: true);
            Assert.NotSame(runtime.GetType("Sunder.Package.Agent.Contracts.Models.AgentShellSyntaxKinds"), presentation.GetType("Sunder.Package.Agent.Contracts.Models.AgentShellSyntaxKinds"));
            foreach (var assembly in new[] { runtime, presentation })
            {
                Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Agent.Execution.Docker.Protocol");
                Assert.Null(assembly.GetType("Sunder.Package.Agent.Execution.Docker.DockerExecutionOperationRequest"));
                foreach (var name in new[] { "DockerExecutionOperationResponse", "DockerImageDefinition", "DockerExecutionPresentationDefaults" })
                    Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Execution.Docker." + name, true)!.Assembly);
            }
            Assert.DoesNotContain(presentation.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Execution.Docker" or "Sunder.Sdk.Worker");
            var runnerType = await BuildDockerBackend(root, worker, load);
            using var package = RegressionTestPackageScope.Create();
            var physicalRoot = OperatingSystem.IsMacOS() && package.RootPath.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + package.RootPath : package.RootPath;
            var context = new TestPackageContext("younics.ai.agent.execution.docker", physicalRoot, version: "2.0.0", storage: package.Context.Storage);
            var backend = new DockerArtifactBackend();
            Func<string[], int, CancellationToken, string?, Task<(int, string, bool, bool)>> run = backend.Run;
            var runner = Activator.CreateInstance(runnerType, context, run)!;
            Action<IServiceCollection> configure = services => services.AddSingleton(runtime.GetType("Sunder.Package.Agent.Execution.Docker.DockerCliRunner", true)!, runner);
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var rpc = new RegressionTestRpcClient(state, "younics.ai.agent.execution.docker");
            var options = CreateControlledGeneratedWorker(runnerType.Assembly, context, rpc, configure).Options;
            await using var composition = Assert.IsType<SunderWorkerComposition>(options.Composition);
            Assert.Empty(backend.Calls);
            var validation = (ISunderWorkerLifecycleService)composition.Services.GetRequiredService(runtime.GetType("Sunder.Package.Agent.Execution.Docker.DockerPackageStorageValidation", true)!);
            await validation.PrepareCandidateAsync(); Assert.Empty(backend.Calls);
            await validation.ActivateAsync(null!);
            var registrations = options.Providers.ToDictionary(value => value.ProviderId);
            Assert.Equal(new[] { "docker.execution.target", "docker.presentation", "docker.stack", "docker.workspace.editor" }, registrations.Keys.Order(StringComparer.Ordinal));
            foreach (var provider in options.Providers)
            {
                var descriptor = provider.ProviderId == "docker.presentation" ? CurrentPrivateRpcDescriptors.Get("younics.ai.agent.execution.docker.presentation")
                    : provider.ProviderId == "docker.stack" ? SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Path.Combine(BuilderSourceMigrationTests.FindRepository(),
                        "../sunder-core/languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/Contracts/sunder.stack.contributor.rpc.json"))) : CurrentPublicRpcDescriptors.Get(provider.ContractId);
                Assert.Equal(descriptor.Sha256, provider.DescriptorSha256); Assert.Equal(descriptor.AbiSha256, provider.AbiSha256);
                state.Add("younics.ai.agent.execution.docker", descriptor, provider.Handler, provider.Handler, Noop.Instance, provider.ProviderId, "2.0.0");
            }
            await using var appServices = CreateGeneratedApp(presentation, context, rpc, new CompositionBackgroundTaskQueue());
            var presentationContract = presentation.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Execution_Docker_Services.IPresentationService", true)!;
            var client = appServices.GetRequiredService(presentationContract);
            Assert.Null(presentation.GetType("Sunder.Package.Agent.Execution.Docker.App.DockerExecutionSourceClient"));
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
            var binding = new AgentWorkspaceBindingRecord("binding.docker", workspace.WorkspaceId, RegressionTestBindings.ExecutionTargets, "docker", "primary-execution-target", true, 0, now, now);
            var editor = new AgentWorkspaceEditorContext(
                AgentRoleBoundary.As<AgentApp::Sunder.Package.Agent.Contracts.Models.AgentWorkspaceRecord>(workspace), "docker", binding.BindingId);
            await using var editorReceiver = new GeneratedServiceTestScope(rpc.ForCaller("younics.ai.agent"));
            var editorClient = Assert.Single((await editorReceiver.Providers<AppServices.IWorkspaceEditorService>().ListAsync()).Providers,
                provider => provider.ProviderId == "docker.workspace.editor").Service;
            Assert.True(await editorClient.CanEditAsync(editor)); Assert.Single(await editorClient.GetSectionsAsync(editor));
            Assert.True((await editorClient.SaveSectionAsync(editor,
                new("docker-execution-settings", new Dictionary<string, AgentEditorFieldValue> { ["image"] = new(image), ["shell-path"] = new("/bin/sh") }))).Success);
            Assert.Single((await Rpc("docker.stack", "stack-contributor", "list-export-items", new { packageId = context.PackageId })).GetProperty("items").EnumerateArray());
            var exported = await Rpc("docker.stack", "stack-contributor", "export", new StackExportRequest([new("docker-images")]));
            Assert.Contains(image, Assert.Single(exported.GetProperty("fragments").EnumerateArray()).GetProperty("jsonPayload").GetString()!, StringComparison.Ordinal);
            var executionContext = new AgentExecutionTargetContext(Guid.NewGuid(), "profile.docker", workspace, binding);
            await using var executionReceiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, "younics.ai.agent"));
            var generatedExecution = Assert.Single((await executionReceiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers,
                provider => provider.ProviderId == "docker.execution.target").Service;
            var absent = await Assert.ThrowsAsync<SunderRpcException>(() => generatedExecution.ResolveResourcesAsync(executionContext, []).AsTask());
            Assert.Equal(SunderRpcErrorKind.Domain, absent.Error.Kind); Assert.Equal("agent.execution.facet-unavailable", absent.Error.Code);
            var executionRpc = new FilesRpcObserver(new RegressionTestRpcClient(state, "younics.ai.agent"));
            await using var executionScope = new GeneratedServiceTestScope(executionRpc);
            var executionClient = Assert.Single((await executionScope.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers,
                provider => provider.ProviderId == "docker.execution.target").Service;
            await VerifyDockerExecution(executionContext, workspaceRoot, backend, executionClient);
            executionRpc.AssertScopedOperations(); Assert.All(executionRpc.Scopes, scope => Assert.True(scope.Disposed));
            Assert.Empty((await Present(new { kind = "delete-image", imageReference = image })).GetProperty("images").EnumerateArray());
            await state.RetireAsync(registrations["docker.presentation"].Handler);
            Assert.Equal("rpc.provider-not-found", (await Assert.ThrowsAsync<SunderRpcException>(() => Present(new { kind = "get-settings" }))).Error.Code);
            // Probe transport-sized projections independently of the smaller state-value bound.
            var images = Activator.CreateInstance(runnerType.Assembly.GetType("ArtifactImageCatalog", true)!)!;
            var presentationType = runtime.GetType("Sunder.Package.Agent.Execution.Docker.DockerExecutionPresentationService", true)!;
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Execution.Docker.Rpc.ManagementService"));
            await using var managementReceiver = new GeneratedServiceTestScope(rpc);
            var catalog = await AgentRoleBoundary.Await(images.GetType().GetMethod("CreateAsync")!.Invoke(images, [context, runner, CancellationToken.None])!)!;
            var presentationService = AgentRoleBoundary.Create(presentationType, context, runner, catalog);
            var management = presentationService;
            var managementRegistration = managementReceiver.Register(management, "younics.ai.agent.execution.docker.presentation");
            state.Add("younics.ai.agent.execution.docker", CurrentPrivateRpcDescriptors.Get("younics.ai.agent.execution.docker.presentation"), management, managementRegistration.Handler, Noop.Instance, managementRegistration.ProviderId, "2.0.0");
            var large = await Present(new { kind = "get-settings" });
            Assert.Equal(2500, large.GetProperty("images").GetArrayLength());
            state.FailNextResponseContentRegistration(new IOException("private docker content failure"));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => Present(new { kind = "get-settings" }))).Error.Kind);
            await state.RetireAsync(management);
            images.GetType().GetProperty("Count")!.SetValue(images, 8500);
            catalog = await AgentRoleBoundary.Await(images.GetType().GetMethod("CreateAsync")!.Invoke(images, [context, runner, CancellationToken.None])!)!;
            presentationService = AgentRoleBoundary.Create(presentationType, context, runner, catalog);
            management = presentationService;
            managementRegistration = managementReceiver.Register(management, "younics.ai.agent.execution.docker.presentation");
            state.Add("younics.ai.agent.execution.docker", CurrentPrivateRpcDescriptors.Get("younics.ai.agent.execution.docker.presentation"), management, managementRegistration.Handler, Noop.Instance, managementRegistration.ProviderId, "2.0.0");
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => Present(new { kind = "get-settings" }))).Error.Kind);
            await state.RetireAsync(management);
            Assert.Equal("rpc.provider-not-found", (await Assert.ThrowsAsync<SunderRpcException>(() => Present(new { kind = "get-settings" }))).Error.Code);
            await composition.DisposeAsync();
            if (!OperatingSystem.IsWindows()) Assert.True(backend.Stops > 0);

            async Task<JsonElement> Present(object request, CancellationToken token = default)
            {
                var value = JsonSerializer.SerializeToElement(request, MemoryProviderJson);
                var method = presentationContract.GetMethod(value.GetProperty("kind").GetString() switch
                {
                    "get-settings" => "GetSettingsAsync", "save-settings" => "SaveSettingsAsync",
                    "add-image" => "AddImageAsync", "refresh-image" => "RefreshImageAsync",
                    "refresh-images" => "RefreshImagesAsync", "pull-image" => "PullImageAsync",
                    "test-docker" => "TestDockerAsync", "delete-image" => "DeleteImageAsync",
                    _ => throw new InvalidOperationException("Unknown Docker presentation fixture operation."),
                })!;
                var arguments = method.GetParameters().Select(parameter => parameter.ParameterType == typeof(CancellationToken)
                    ? (object)token : value.TryGetProperty(parameter.Name!, out var member) ? member.Deserialize(parameter.ParameterType, MemoryProviderJson) : null).ToArray();
                var pending = method.Invoke(client, arguments)!;
                var task = (Task)pending.GetType().GetMethod("AsTask")!.Invoke(pending, null)!;
                await task.WaitAsync(TimeSpan.FromSeconds(15));
                var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
                if (result is SunderContent content)
                {
                    await using var contentLifetime = content;
                    Assert.Equal("application/json", content.MediaType);
                    var reader = presentation.GetType("Sunder.Package.Agent.Execution.Docker.DockerPresentationContent", true)!
                        .GetMethod("ReadAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
                    result = (await AgentRoleBoundary.Await(reader.Invoke(null, [content, token])!))!;
                }
                Assert.Same(presentation, result.GetType().Assembly);
                return JsonSerializer.SerializeToElement(result, result.GetType(), MemoryProviderJson);
            }
            async Task<JsonElement> Rpc(string id, string service, string method, object request, CancellationToken token = default)
            {
                var selected = Assert.Single((await rpc.DiscoverAsync(registrations[id].ContractId, token)).Providers, value => value.ProviderId == id);
                return await rpc.InvokeAsync(selected.Endpoint, service, method, JsonSerializer.SerializeToElement(request, MemoryProviderJson), cancellationToken: token).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
        finally { load.Unload(); }
    }
}
