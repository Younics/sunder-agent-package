using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyAgentPermissionSurfaces(Assembly owner, Assembly files, Assembly shell)
    {
        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        var (_, ownerLifetime, ownerServices) = CreateGeneratedWorker(owner, package.Context, rpc);
        await using var ownedOwner = ownerLifetime;
        var permissionServiceType = owner.GetType(typeof(AgentServices.IPermissionSurfaceService).FullName!, true)!;
        await using var surfacesScope = new GeneratedServiceTestScope(rpc);
        var surfaces = surfacesScope.Providers(permissionServiceType);
        var storeType = owner.GetType("Sunder.Package.Agent.Storage.AgentLocalStore", true)!;
        var store = Activator.CreateInstance(storeType, package.Context)!;
        await using var initialStore = (IAsyncDisposable)store;
        var serviceType = owner.GetType("Sunder.Package.Agent.Services.AgentPermissionService", true)!;
        var permissions = AgentRoleBoundary.Create(serviceType, store, surfaces);
        Assert.Null(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedPermissionSurfaceClient"));
        Assert.Null(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.AgentMetadataBindings"));
        var generic = Assert.Single(await ActionsAsync());
        Assert.Equal("agent.tool.mutate", generic.ActionId);
        Assert.Equal(AgentPermissionDecision.Ask, Assert.Single(generic.Boundaries).DefaultDecision);
        Assert.Equal(AgentPermissionDecision.Deny, (await EvaluateAsync("", "")).Decision);
        Assert.Equal(AgentPermissionDecisionSource.UnknownBoundary, (await EvaluateAsync("files.read", AgentPermissionBoundaryIds.ConfiguredScope)).Source);

        var filesContext = new TestPackageContext("sunder.package.agent.tools.files", package.RootPath, version: "2.0.0", storage: package.Context.Storage);
        var (filesOptions, filesLifetime, _) = CreateGeneratedWorker(files, filesContext, rpc);
        await using var ownedFiles = filesLifetime;
        var filesRegistration = Assert.Single(filesOptions.Providers, value => value.ProviderId == "files.permissions");
        var filesHandler = AddSurfaceProvider(state, filesContext.PackageId, filesRegistration);
        var shellContext = new TestPackageContext("sunder.package.agent.tools.shell", package.RootPath, version: "2.0.0", storage: package.Context.Storage);
        var (shellOptions, shellLifetime, _) = CreateGeneratedWorker(shell, shellContext, rpc);
        await using var ownedShell = shellLifetime;
        var shellRegistration = Assert.Single(shellOptions.Providers, value => value.ProviderId == "shell.permissions");
        var shellHandler = AddSurfaceProvider(state, "sunder.package.agent.tools.shell", shellRegistration);
        var actions = await ActionsAsync(); Assert.Equal(5, actions.Count);
        Assert.Equal(actions.OrderBy(action => action.DisplayName, StringComparer.OrdinalIgnoreCase), actions);
        foreach (var action in actions.Where(action => action.ActionId.StartsWith("files.", StringComparison.Ordinal)))
        {
            Assert.Equal(3, action.Boundaries.Count); Assert.NotEmpty(action.Description);
            Assert.Equal(AgentPermissionDecision.Allow, Assert.Single(action.Boundaries, value => value.BoundaryId == AgentPermissionBoundaryIds.ConfiguredScope).DefaultDecision);
            Assert.Equal(AgentPermissionDecision.Ask, Assert.Single(action.Boundaries, value => value.BoundaryId == AgentPermissionBoundaryIds.OutsideConfiguredScope).DefaultDecision);
        }
        Assert.Equal(AgentPermissionDecision.Allow, (await EvaluateAsync("FILES.READ", AgentPermissionBoundaryIds.ConfiguredScope)).Decision);
        Assert.Equal(AgentPermissionDecision.Ask, (await EvaluateAsync("files.mutate", AgentPermissionBoundaryIds.OutsideConfiguredScope)).Decision);
        Assert.Equal(AgentPermissionDecision.Ask, (await EvaluateAsync("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget)).Decision);
        Assert.Equal(AgentPermissionDecision.Ask, (await EvaluateAsync(generic.ActionId, generic.Boundaries[0].BoundaryId)).Decision);
        Assert.Equal(AgentPermissionDecisionSource.UnknownBoundary, (await EvaluateAsync("files.read", "new-boundary")).Source);

        var workspaces = ownerServices.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentWorkspaceService", true)!);
        var workspace = (AgentWorkspaceRecord)SurfaceCall(workspaces, "CreateWorkspace", "Permission fixture")!;
        var sessions = ownerServices.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentSessionService", true)!);
        var session = SurfaceCall(sessions, "CreateSession", "Permissions", null, null, null, null, null, null, null, null, null, workspace.WorkspaceId)!;
        var sessionId = (Guid)session.GetType().GetProperty("SessionId")!.GetValue(session)!;
        SurfaceCall(permissions, "SaveOverride", "files.mutate", AgentPermissionBoundaryIds.OutsideConfiguredScope, AgentPermissionDecision.Deny);
        SurfaceCall(permissions, "SetSessionUnrestrictedMode", sessionId, true);
        var denied = await EvaluateAsync("files.mutate", AgentPermissionBoundaryIds.OutsideConfiguredScope, sessionId);
        Assert.Equal(AgentPermissionDecision.Deny, denied.Decision); Assert.Equal(AgentPermissionDecisionSource.ConfiguredOverride, denied.Source);
        Assert.Equal(AgentPermissionDecisionSource.UnrestrictedMode, (await EvaluateAsync("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget, sessionId)).Source);
        await initialStore.DisposeAsync(); store = Activator.CreateInstance(storeType, package.Context)!;
        await using var reopened = (IAsyncDisposable)store;
        permissions = AgentRoleBoundary.Create(serviceType, store, surfaces);
        Assert.Equal(AgentPermissionDecision.Deny, (await EvaluateAsync("files.mutate", AgentPermissionBoundaryIds.OutsideConfiguredScope, sessionId)).Decision);
        SurfaceCall(permissions, "DeleteOverride", "files.mutate", AgentPermissionBoundaryIds.OutsideConfiguredScope);
        Assert.Equal(AgentPermissionDecisionSource.UnrestrictedMode, (await EvaluateAsync("files.mutate", AgentPermissionBoundaryIds.OutsideConfiguredScope, sessionId)).Source);
        SurfaceCall(permissions, "SetSessionUnrestrictedMode", sessionId, false);
        Assert.Equal(AgentPermissionDecision.Ask, (await EvaluateAsync("files.mutate", AgentPermissionBoundaryIds.OutsideConfiguredScope, sessionId)).Decision);

        var observed = new FilesRpcObserver(rpc, "describe");
        await using var observedScope = new GeneratedServiceTestScope(observed);
        var observedCatalog = observedScope.Providers(permissionServiceType);
        var client = await FilesPermissionServiceAsync(observedCatalog);
        var descriptor = await client.DescribeAsync();
        Assert.NotEmpty(descriptor.SurfaceId); Assert.NotEmpty(descriptor.DisplayName);
        var described = descriptor.Actions; Assert.Equal(3, described.Count);
        Assert.IsAssignableFrom<IList<AgentPermissionActionDescriptor>>(described)[0] =
            described[0] with { DisplayName = "Locally changed" };
        Assert.DoesNotContain((await client.DescribeAsync()).Actions, action => action.DisplayName == "Locally changed");
        observed.Transform = value => value with { ActivationEpoch = value.ActivationEpoch + 1 };
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.DescribeAsync().AsTask())).Error.Kind);
        observed.Transform = null; observed.InvalidateAfterInvoke = true;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.DescribeAsync().AsTask())).Error.Kind);
        observed.InvalidateAfterInvoke = false; observed.Transform = null;

        await state.RetireAsync(filesHandler);
        Assert.Equal(2, (await ActionsAsync()).Count); Assert.Equal(AgentPermissionDecision.Ask, (await EvaluateAsync("files.read", AgentPermissionBoundaryIds.ConfiguredScope)).Decision);
        filesHandler = AddSurfaceProvider(state, filesContext.PackageId, filesRegistration);
        Assert.Equal(5, (await ActionsAsync()).Count); Assert.Equal(AgentPermissionDecision.Allow, (await EvaluateAsync("files.read", AgentPermissionBoundaryIds.ConfiguredScope)).Decision);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.DescribeAsync().AsTask())).Error.Kind);
        var current = await FilesPermissionServiceAsync(observedCatalog);

        // A valid deny default is mapped by its wire name; case-insensitive action
        // deduplication keeps the first provider's definition.
        await state.RetireAsync(shellHandler);
        shellHandler = AddSurfaceProvider(state, "sunder.package.agent.tools.shell", shellRegistration);
        shellHandler.Transform = (_, value) =>
        {
            var node = JsonNode.Parse(value.GetRawText())!;
            node["actions"]![0]!["boundaries"]![0]!["defaultDecision"] = "deny";
            var duplicate = JsonSerializer.SerializeToNode(actions.Single(action => action.ActionId == "files.read"), MemoryProviderJson)!;
            duplicate["actionId"] = "FILES.READ"; duplicate["displayName"] = "Duplicate";
            ((JsonArray)node["actions"]!).Add(duplicate);
            return JsonSerializer.SerializeToElement(node);
        };
        Assert.Equal(5, (await ActionsAsync()).Count); Assert.DoesNotContain(await ActionsAsync(), action => action.DisplayName == "Duplicate");
        Assert.Equal(AgentPermissionDecision.Deny, (await EvaluateAsync("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget)).Decision);
        filesHandler.Transform = (_, value) =>
        {
            var node = JsonNode.Parse(value.GetRawText())!; node["actions"]![0]!["boundaries"]![0]!["defaultDecision"] = "permit";
            return JsonSerializer.SerializeToElement(node);
        };
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, (await Assert.ThrowsAsync<SunderRpcException>(() => current.DescribeAsync().AsTask())).Error.Kind);
        filesHandler.Transform = null; filesHandler.BlockMethod = "describe"; filesHandler.Gate = new();
        var pending = current.DescribeAsync().AsTask();
        await filesHandler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = observedScope.DisposeAsync().AsTask(); Assert.Same(stopped, observedScope.DisposeAsync().AsTask());
        var retiredCall = await Assert.ThrowsAsync<SunderRpcException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(SunderRpcErrorKind.Unavailable, retiredCall.Error.Kind);
        Assert.Equal("rpc.binding.retired", retiredCall.Error.Code);
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => current.DescribeAsync().AsTask());
        observed.AssertScopedOperations(); Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));

        async Task<IReadOnlyList<AgentPermissionActionDescriptor>> ActionsAsync()
            => (IReadOnlyList<AgentPermissionActionDescriptor>)AgentRoleBoundary.AssertionValue(
                await AgentRoleBoundary.Await(AgentRoleBoundary.Call(permissions, "ListActionsAsync", CancellationToken.None)!))!;

        async Task<AgentPermissionEvaluation> EvaluateAsync(string action, string boundary, Guid? id = null)
            => (AgentPermissionEvaluation)AgentRoleBoundary.AssertionValue(
                await AgentRoleBoundary.Await(AgentRoleBoundary.Call(permissions, "EvaluateAsync", id,
                    new AgentPermissionRequest(action, boundary, "Fixture permission"), CancellationToken.None)!))!;

        static async Task<AgentServices.IPermissionSurfaceService> FilesPermissionServiceAsync(object providers)
        {
            var snapshot = (await AgentRoleBoundary.Await(AgentRoleBoundary.Call(providers, "ListAsync", CancellationToken.None)!))!;
            var entries = ((IEnumerable)snapshot.GetType().GetProperty("Providers")!.GetValue(snapshot)!).Cast<object>();
            var selected = Assert.Single(entries, entry => (string)entry.GetType().GetProperty("ProviderId")!.GetValue(entry)! == "files.permissions");
            return AgentRoleBoundary.As<AgentServices.IPermissionSurfaceService>(selected.GetType().GetProperty("Service")!.GetValue(selected)!);
        }
    }
}
