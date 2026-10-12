using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using Xunit;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyBuilderRoles(string root, string worker)
    {
        var load = new AssemblyLoadContext("builder-role-local", isCollectible: true);
        try
        {
            var runtime = Load(worker, "Sunder.Package.Agent.Builder", runtimeRole: true, singleProject: true);
            var presentation = Load(worker, "Sunder.Package.Agent.Builder.App", runtimeRole: false, singleProject: true);
            AssertBuilderSemanticOwnership(runtime, app: false);
            AssertBuilderSemanticOwnership(presentation, app: true);
            Assert.NotSame(runtime.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentWorkspaceRecord", true), presentation.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentWorkspaceRecord", true));
            var local = Load(Path.Combine(root, "LocalWorker"), "Sunder.Package.Agent.Execution.Local", runtimeRole: true, singleProject: true);
            var owner = Load(Path.Combine(root, "Worker"), "Sunder.Package.Agent", runtimeRole: true, singleProject: true);
            foreach (var assembly in new[] { runtime, presentation })
            {
                Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Agent.Builder.Protocol");
                foreach (var name in new[] { "BuilderRpcRequest", "BuilderRpcResponse", "BuilderRpcPayloadLimits" })
                    Assert.Null(assembly.GetType("Sunder.Package.Agent.Builder." + name));
                foreach (var name in new[] { "BuilderProcessResult", "BuilderWorkspaceExecutionProjection", "BuilderContent" })
                    Assert.Same(assembly, assembly.GetType("Sunder.Package.Agent.Builder." + name, true)!.Assembly);
            }
            Assert.DoesNotContain(presentation.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Builder" or "Sunder.Sdk.Worker");
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Builder.BuilderRpcProvider")); Assert.Null(runtime.GetType("Sunder.Package.Agent.Builder.BuilderRpcExecutor"));
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Builder.BuilderViewModel"));
            Assert.Null(presentation.GetType("Sunder.Package.Agent.Builder.BuilderRuntimeService"));
            Assert.Null(presentation.GetType("Sunder.Package.Agent.Builder.BuilderAppBindings"));
            using var package = RegressionTestPackageScope.Create();
            var physical = OperatingSystem.IsMacOS() && package.RootPath.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + package.RootPath : package.RootPath;
            var workspaceRoot = Directory.CreateDirectory(Path.Combine(physical, "workspace")).FullName;
            var context = new TestPackageContext("sunder.package.agent.builder", physical, version: "2.0.0", storage: package.Context.Storage);
            var localContext = new TestPackageContext("sunder.package.agent.execution.local", physical, version: "2.0.0", storage: package.Context.Storage);
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var (localOptions, localLifetime, _) = CreateGeneratedWorker(local, localContext, new RegressionTestRpcClient(state, localContext.PackageId));
            await using var ownedLocal = localLifetime;
            var targetRegistration = Assert.Single(localOptions.Providers, value => value.ProviderId == "local.execution.target");
            var process = new BuilderProcessProbe(targetRegistration.Handler, workspaceRoot);
            var targetDescriptor = CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ExecutionTargets);
            state.Add(localContext.PackageId, targetDescriptor, process, process, Noop.Instance, targetRegistration.ProviderId, "2.0.0");
            var now = DateTimeOffset.UtcNow;
            var workspace = new AgentWorkspaceRecord("workspace.builder", "Builder workspace", null, now, now,
                [new("path.builder", "workspace.builder", workspaceRoot, true, 0, now, now)]);
            var binding = new AgentWorkspaceBindingRecord("binding.builder", workspace.WorkspaceId, RegressionTestBindings.ExecutionTargets, "local", "primary-execution-target", true, 0, now, now);
            var targetContext = new AgentExecutionTargetContext(null, null, workspace, binding);
            var targetEndpoint = Assert.Single(state.Discover(targetDescriptor.ContractId).Providers);
            var targetRpc = new RegressionTestRpcClient(state, "sunder.package.agent");
            await using var targetReceiver = new GeneratedServiceTestScope(targetRpc);
            var targetReference = Assert.Single((await targetReceiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers).Reference;
            var description = await targetReference.Service.DescribeAsync();
            var scope = await targetReference.Service.ExecutionScopeAsync(targetContext);
            var resolver = new BuilderArtifactResolver(workspace, binding, description, scope, targetReference);
            Assert.True(owner.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Services.IWorkspaceExecutionResolverService", true)!.IsInterface);
            var resolverService = resolver;
            using var resolverProviders = new RegressionTestSourceProviders();
            var resolverRegistration = resolverProviders.Create(RegressionTestBindings.WorkspaceExecutionResolver, resolver, targetReceiver);
            var resolverDescriptor = CurrentPublicRpcDescriptors.Get(RegressionTestBindings.WorkspaceExecutionResolver);
            var observer = new FilesRpcObserver(new RegressionTestRpcClient(state, context.PackageId), "execute-process");
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Builder.BuilderWorkerComposition"));
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Builder.BuilderWorkerBindings"));
            var (options, lifetime, runtimeServices) = CreateGeneratedWorker(runtime, context, observer);
            await using var owned = lifetime;
            Assert.Null(options.Composition);
            Assert.Null(runtime.GetType("Sunder.Package.Agent.Builder.Rpc.BuilderExecutionBindings"));
            Assert.NotNull(runtimeServices.GetService(typeof(IServiceProviders<>).MakeGenericType(
                runtime.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Services.IWorkspaceExecutionResolverService", true)!)));
            var registration = Assert.Single(options.Providers);
            var descriptor = CurrentPrivateRpcDescriptors.Get("sunder.package.agent.builder.runtime");
            Assert.Equal(descriptor.Sha256, registration.DescriptorSha256); Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
            state.Add(context.PackageId, descriptor, registration.Handler, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
            var client = new RegressionTestRpcClient(state, context.PackageId);
            await using var appServices = CreateGeneratedApp(presentation, context, client, new BuilderArtifactQueue());
            var execution = appServices.GetRequiredService(presentation.GetType("Sunder.Package.Agent.Builder.BuilderWorkspaceExecutionService", true)!);
            var appOperations = appServices.GetRequiredService(presentation.GetType("Sunder.Package.Agent.Builder.BuilderProjectApplicationService", true)!);
            Assert.Same(appServices.GetRequiredService(presentation.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Builder_Services.IBuilderRuntimeService", true)!),
                execution.GetType().GetField("_runtime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(execution));
            Assert.Empty(observer.Scopes); Assert.Equal(0, observer.ActiveWatches);
            Assert.Null(options.OnActivated);
            Assert.Equal("agent.builder.workspace-execution-unavailable", (await Assert.ThrowsAsync<SunderRpcException>(() => Call(execution, "ListWorkspacesAsync", CancellationToken.None))).Error.Code);
            RegisterResolver();
            Assert.Single((IEnumerable)(await Call(execution, "ListWorkspacesAsync", CancellationToken.None))!);
            var resolved = (await Call(execution, "ResolveAsync", workspace.WorkspaceId, CancellationToken.None))!;
            Assert.Same(presentation, resolved.GetType().Assembly);
            var mapped = JsonSerializer.SerializeToElement(await Call(resolved, "MapToHostPathAsync", workspaceRoot, CancellationToken.None), MemoryProviderJson)
                .Deserialize<AgentExecutionPathMapping>(MemoryProviderJson)!;
            Assert.True(mapped.IsInsideAllowedRoot); Assert.Equal(workspaceRoot, mapped.HostPath);
            await Call(resolved, "AddPathEntryAsync", workspaceRoot, CancellationToken.None);
            var echo = OperatingSystem.IsWindows() ? "echo builder-shell" : "printf builder-shell";
            var shellResult = await Call(resolved, "RunShellAsync", echo, workspaceRoot, 5, CancellationToken.None);
            Assert.Contains("builder-shell", shellResult!.GetType().GetProperty("StandardOutput")!.GetValue(shellResult)!.ToString()!, StringComparison.Ordinal);
            var projectType = presentation.GetType("Sunder.Package.Agent.Builder.BuilderProjectRecord", true)!;
            var project = JsonSerializer.SerializeToElement(new { id = "artifact", displayName = "Artifact Project", packageId = "local.artifact.builder", workspaceId = workspace.WorkspaceId,
                executionProjectFolder = "", projectFolder = "", createdAtUtc = now, updatedAtUtc = now, workspacePathId = workspace.Paths[0].PathId }, MemoryProviderJson).Deserialize(projectType, MemoryProviderJson)!;
            var background = new BackgroundProcessContext(CancellationToken.None, _ => { }, (_, _) => { }, _ => { });
            var workspacePath = JsonSerializer.SerializeToElement(workspace.Paths[0], MemoryProviderJson)
                .Deserialize(presentation.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentWorkspacePathRecord", true)!, MemoryProviderJson)!;
            var initialized = (await Call(appOperations, "InitializeProjectAsync", project, workspacePath, background, null))!;
            project = initialized.GetType().GetProperty("Project")!.GetValue(initialized)!;
            Assert.True(Directory.Exists((string)projectType.GetProperty("ProjectFolder")!.GetValue(project)!));
            Assert.All(((IEnumerable)initialized.GetType().GetProperty("Prerequisites")!.GetValue(initialized)!).Cast<object>(), item => Assert.True((bool)item.GetType().GetProperty("IsInstalled")!.GetValue(item)!));
            await Call(appOperations, "BuildProjectAsync", project, background);
            var published = (await Call(appOperations, "PublishProjectAsync", project, background))!;
            Assert.Contains("Package archive created", published.GetType().GetProperty("RuntimeLog")!.GetValue(published)!.ToString()!, StringComparison.Ordinal);
            var projectOperations = process.Processes.Where(request => request.Arguments[0] is "restore" or "build" or "msbuild").ToArray();
            Assert.Equal(new[] { "restore", "build", "msbuild" }, projectOperations.Select(request => request.Arguments[0]).ToArray());
            var projectFolder = (string)projectType.GetProperty("ExecutionProjectFolder")!.GetValue(project)!;
            Assert.Equal(new[] { "restore", projectFolder }, projectOperations[0].Arguments);
            Assert.Equal(new[] { "build", projectFolder }, projectOperations[1].Arguments);
            Assert.All(projectOperations, request => Assert.Equal(projectFolder, request.WorkingDirectory));
            Assert.Contains(process.Processes, request => request.TimeoutSeconds == 900 && request.Arguments.SequenceEqual(new[]
                { "msbuild", "-t:CheckSunderContracts;FreezeSunderContracts;PackSunderPackage", "-p:Configuration=Release" }));
            Assert.DoesNotContain(process.Processes, request => request.Arguments[0] == "publish" || request.Arguments.Contains("--createInPlace"));
            process.FailBuild = true;
            var buildFailure = await Assert.ThrowsAnyAsync<Exception>(() => Call(appOperations, "BuildProjectAsync", project, background));
            Assert.Equal("BuilderProjectOperationException", buildFailure.GetType().Name);
            Assert.Contains("Controlled build failed", buildFailure.GetType().GetProperty("RuntimeLog")!.GetValue(buildFailure)!.ToString()!, StringComparison.Ordinal);
            process.FailBuild = false;
            var store = appServices.GetRequiredService(presentation.GetType("Sunder.Package.Agent.Builder.BuilderProjectStore", true)!);
            var projects = Array.CreateInstance(projectType, 1); projects.SetValue(project, 0);
            await Call(store, "SaveAsync", projects, CancellationToken.None);
            Assert.Single((IEnumerable)(await Call(store, "LoadAsync", CancellationToken.None))!);
            resolver.CatalogCount = 2500;
            Assert.Equal(2500, ((IEnumerable)(await Call(execution, "ListWorkspacesAsync", CancellationToken.None))!).Cast<object>().Count());
            resolver.CatalogCount = 8500;
            Assert.Equal("agent.builder.response-too-large", (await Assert.ThrowsAsync<SunderRpcException>(() => Call(execution, "ListWorkspacesAsync", CancellationToken.None))).Error.Code);
            resolver.CatalogCount = 1;
            state.FailNextResponseContentRegistration(new IOException("private-store-failure"));
            var contentFailure = await Assert.ThrowsAsync<SunderRpcException>(() => Call(execution, "ListWorkspacesAsync", CancellationToken.None));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, contentFailure.Error.Kind); Assert.DoesNotContain("private-store-failure", contentFailure.Message, StringComparison.Ordinal);
            process.EmptyFacets = true;
            Assert.Equal("agent.builder.unsupported-operation", (await Assert.ThrowsAsync<SunderRpcException>(() => Call(resolved, "RunProcessAsync", "dotnet", new[] { "--list-sdks" }, workspaceRoot, 5, CancellationToken.None))).Error.Code);
            process.EmptyFacets = false;
            process.BlockProcess = new FilesCallGate();
            using (var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                var pending = Call(appOperations, "BuildProjectAsync", project, new BackgroundProcessContext(cancelled.Token, _ => { }, (_, _) => { }, _ => { }));
                await process.BlockProcess.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                await process.BlockProcess.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            process.BlockProcess = new FilesCallGate();
            var retiring = Call(appOperations, "BuildProjectAsync", project, background);
            await process.BlockProcess.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var retired = state.RetireAsync(process);
            Assert.Equal("agent.builder.execution-target-unavailable", (await Assert.ThrowsAsync<SunderRpcException>(() => retiring)).Error.Code);
            await retired.WaitAsync(TimeSpan.FromSeconds(5));
            process = new BuilderProcessProbe(targetRegistration.Handler, workspaceRoot);
            state.Add(localContext.PackageId, targetDescriptor, process, process, Noop.Instance, targetRegistration.ProviderId, "2.0.0");
            // The resolver is now returning an already-retired required reference:
            // generated output validation rejects that provider result before Builder
            // can use it. It must not silently bind the replacement target.
            var staleResolution = await Assert.ThrowsAsync<SunderRpcException>(() => Call(execution, "ResolveAsync", workspace.WorkspaceId, CancellationToken.None));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, staleResolution.Error.Kind);
            Assert.Equal("rpc.provider.handler-fault", staleResolution.Error.Code);
            Assert.Empty(process.Processes);
            resolver.ExecutionTarget = Assert.Single((await targetReceiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers).Reference;
            await Call(appOperations, "BuildProjectAsync", project, background);
            resolver.BlockList = new FilesCallGate();
            var listing = Call(execution, "ListWorkspacesAsync", CancellationToken.None);
            await resolver.BlockList.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var resolverRetirement = state.RetireAsync(resolverService);
            Assert.Equal("agent.builder.workspace-execution-unavailable", (await Assert.ThrowsAsync<SunderRpcException>(() => listing)).Error.Code);
            await resolverRetirement.WaitAsync(TimeSpan.FromSeconds(5)); resolver.BlockList = null;
            RegisterResolver();
            Assert.Single((IEnumerable)(await Call(execution, "ListWorkspacesAsync", CancellationToken.None))!);
            process.BlockProcess = new FilesCallGate();
            var stopping = Call(appOperations, "BuildProjectAsync", project, background);
            await process.BlockProcess.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = state.RetireAsync(registration.Handler);
            var shutdownError = await Assert.ThrowsAsync<SunderRpcException>(() => stopping.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, shutdownError.Error.Kind);
            Assert.Equal("test.rpc.stale-endpoint", shutdownError.Error.Code);
            await process.BlockProcess.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            await lifetime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await lifetime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, observer.ActiveWatches); observer.AssertScopedOperations(); Assert.All(observer.Scopes, scope => Assert.True(scope.Disposed));

            void RegisterResolver() => state.Add("sunder.package.agent", resolverDescriptor, resolverService, resolverRegistration.Handler, Noop.Instance, resolverRegistration.ProviderId, "2.0.0");
            Assembly Load(string directory, string name, bool runtimeRole, bool singleProject = false)
            {
                var output = singleProject ? SingleProjectRoleOutput(directory, runtimeRole ? "worker" : "app")
                    : Path.Combine(directory, "bin/Debug/net10.0", runtimeRole ? RuntimeInformation.RuntimeIdentifier : "");
                using var source = File.OpenRead(Path.Combine(output, name + ".dll"));
                return load.LoadFromStream(source);
            }
        }
        finally { load.Unload(); }
    }

    private static async Task<object?> Call(object instance, string method, params object?[] args)
    {
        object? pending;
        try { pending = AgentRoleBoundary.Call(instance, method, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        var task = pending as Task ?? (Task)pending!.GetType().GetMethod("AsTask")!.Invoke(pending, null)!;
        await task.WaitAsync(TimeSpan.FromSeconds(20));
        return AgentRoleBoundary.AssertionValue(task.GetType().GetProperty("Result")?.GetValue(task));
    }
}
