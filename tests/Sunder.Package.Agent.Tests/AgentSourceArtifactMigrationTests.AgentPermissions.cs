using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyAgentPermissionSurfaces(Assembly owner, Assembly files, Assembly shell)
    {
        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        using var catalog = AgentRpcCatalog.CreateDormant(rpc);
        var storeType = owner.GetType("Sunder.Package.Agent.Storage.AgentLocalStore", true)!;
        var store = Activator.CreateInstance(storeType, package.Context)!;
        await using var initialStore = (IAsyncDisposable)store;
        var serviceType = owner.GetType("Sunder.Package.Agent.Services.AgentPermissionService", true)!;
        var permissions = AgentRoleBoundary.Create(serviceType, store, catalog);
        var clientType = owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedPermissionSurfaceClient", true)!;
        var generic = Assert.Single(Actions());
        Assert.Equal("agent.tool.mutate", generic.ActionId);
        Assert.Equal(AgentPermissionDecision.Ask, Assert.Single(generic.Boundaries).DefaultDecision);
        Assert.Equal(AgentPermissionDecision.Deny, Evaluate("", "").Decision);
        Assert.Equal(AgentPermissionDecisionSource.UnknownBoundary, Evaluate("files.read", AgentPermissionBoundaryIds.ConfiguredScope).Source);

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
        var actions = Actions(); Assert.Equal(5, actions.Count);
        Assert.Equal(actions.OrderBy(action => action.DisplayName, StringComparer.OrdinalIgnoreCase), actions);
        foreach (var action in actions.Where(action => action.ActionId.StartsWith("files.", StringComparison.Ordinal)))
        {
            Assert.Equal(3, action.Boundaries.Count); Assert.NotEmpty(action.Description);
            Assert.Equal(AgentPermissionDecision.Allow, Assert.Single(action.Boundaries, value => value.BoundaryId == AgentPermissionBoundaryIds.ConfiguredScope).DefaultDecision);
            Assert.Equal(AgentPermissionDecision.Ask, Assert.Single(action.Boundaries, value => value.BoundaryId == AgentPermissionBoundaryIds.OutsideConfiguredScope).DefaultDecision);
        }
        Assert.Equal(AgentPermissionDecision.Allow, Evaluate("FILES.READ", AgentPermissionBoundaryIds.ConfiguredScope).Decision);
        Assert.Equal(AgentPermissionDecision.Ask, Evaluate("files.mutate", AgentPermissionBoundaryIds.OutsideConfiguredScope).Decision);
        Assert.Equal(AgentPermissionDecision.Ask, Evaluate("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget).Decision);
        Assert.Equal(AgentPermissionDecision.Ask, Evaluate(generic.ActionId, generic.Boundaries[0].BoundaryId).Decision);
        Assert.Equal(AgentPermissionDecisionSource.UnknownBoundary, Evaluate("files.read", "new-boundary").Source);

        var workspaces = AgentRoleBoundary.Create(owner.GetType("Sunder.Package.Agent.Services.AgentWorkspaceService", true)!, store, catalog, null);
        var workspace = (AgentWorkspaceRecord)SurfaceCall(workspaces, "CreateWorkspace", "Permission fixture")!;
        var sessions = AgentRoleBoundary.Create(owner.GetType("Sunder.Package.Agent.Services.AgentSessionService", true)!, store, catalog);
        var session = SurfaceCall(sessions, "CreateSession", "Permissions", null, null, null, null, null, null, null, null, null, workspace.WorkspaceId)!;
        var sessionId = (Guid)session.GetType().GetProperty("SessionId")!.GetValue(session)!;
        SurfaceCall(permissions, "SaveOverride", "files.mutate", AgentPermissionBoundaryIds.OutsideConfiguredScope, AgentPermissionDecision.Deny);
        SurfaceCall(permissions, "SetSessionUnrestrictedMode", sessionId, true);
        var denied = Evaluate("files.mutate", AgentPermissionBoundaryIds.OutsideConfiguredScope, sessionId);
        Assert.Equal(AgentPermissionDecision.Deny, denied.Decision); Assert.Equal(AgentPermissionDecisionSource.ConfiguredOverride, denied.Source);
        Assert.Equal(AgentPermissionDecisionSource.UnrestrictedMode, Evaluate("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget, sessionId).Source);
        await initialStore.DisposeAsync(); store = Activator.CreateInstance(storeType, package.Context)!;
        await using var reopened = (IAsyncDisposable)store;
        permissions = AgentRoleBoundary.Create(serviceType, store, catalog);
        Assert.Equal(AgentPermissionDecision.Deny, Evaluate("files.mutate", AgentPermissionBoundaryIds.OutsideConfiguredScope, sessionId).Decision);
        SurfaceCall(permissions, "DeleteOverride", "files.mutate", AgentPermissionBoundaryIds.OutsideConfiguredScope);
        Assert.Equal(AgentPermissionDecisionSource.UnrestrictedMode, Evaluate("files.mutate", AgentPermissionBoundaryIds.OutsideConfiguredScope, sessionId).Source);
        SurfaceCall(permissions, "SetSessionUnrestrictedMode", sessionId, false);
        Assert.Equal(AgentPermissionDecision.Ask, Evaluate("files.mutate", AgentPermissionBoundaryIds.OutsideConfiguredScope, sessionId).Decision);

        var observed = new FilesRpcObserver(rpc, "describe");
        var snapshot = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.PermissionSurface)).Providers, value => value.ProviderId == "files.permissions");
        IAgentPermissionSurface Create() => AgentRoleBoundary.As<IAgentPermissionSurface>(Activator.CreateInstance(clientType, observed, snapshot)!);
        var client = Create(); await using var clientLifetime = (IAsyncDisposable)client;
        Assert.NotEmpty(client.SurfaceId); Assert.NotEmpty(client.DisplayName);
        var described = client.ListActions(); Assert.Equal(3, described.Count);
        ((AgentPermissionActionDescriptor[])described)[0] = described[0] with { DisplayName = "Locally changed" };
        Assert.DoesNotContain(client.ListActions(), action => action.DisplayName == "Locally changed");
        observed.Transform = value => value with { ActivationEpoch = value.ActivationEpoch + 1 };
        var stale = Create(); await using var staleLifetime = (IAsyncDisposable)stale;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => stale.ListActions()).Error.Kind);
        observed.Transform = null; observed.InvalidateAfterInvoke = true;
        var late = Create(); await using var lateLifetime = (IAsyncDisposable)late;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => late.ListActions()).Error.Kind);
        observed.InvalidateAfterInvoke = false; observed.Transform = null;

        await state.RetireAsync(filesHandler);
        Assert.Equal(2, Actions().Count); Assert.Equal(AgentPermissionDecision.Ask, Evaluate("files.read", AgentPermissionBoundaryIds.ConfiguredScope).Decision);
        filesHandler = AddSurfaceProvider(state, filesContext.PackageId, filesRegistration);
        Assert.Equal(5, Actions().Count); Assert.Equal(AgentPermissionDecision.Allow, Evaluate("files.read", AgentPermissionBoundaryIds.ConfiguredScope).Decision);
        var old = Create(); await using var oldLifetime = (IAsyncDisposable)old;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => old.ListActions()).Error.Kind);
        snapshot = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.PermissionSurface)).Providers, value => value.ProviderId == "files.permissions");

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
        Assert.Equal(5, Actions().Count); Assert.DoesNotContain(Actions(), action => action.DisplayName == "Duplicate");
        Assert.Equal(AgentPermissionDecision.Deny, Evaluate("shell.execute", AgentPermissionBoundaryIds.SelectedExecutionTarget).Decision);
        filesHandler.Transform = (_, value) =>
        {
            var node = JsonNode.Parse(value.GetRawText())!; node["actions"]![0]!["boundaries"]![0]!["defaultDecision"] = "permit";
            return JsonSerializer.SerializeToElement(node);
        };
        var malformed = Create(); await using var malformedLifetime = (IAsyncDisposable)malformed;
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, Assert.Throws<SunderRpcException>(() => malformed.ListActions()).Error.Kind);
        filesHandler.Transform = null; filesHandler.BlockMethod = "describe"; filesHandler.Gate = new();
        var blocked = Create(); await using var blockedLifetime = (IAsyncDisposable)blocked;
        var pending = Task.Run(() => blocked.ListActions());
        await filesHandler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = blockedLifetime.DisposeAsync().AsTask(); Assert.Same(stopped, blockedLifetime.DisposeAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => blocked.SurfaceId).Error.Kind);
        Assert.Equal(0, observed.UnscopedLookups); Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));

        IReadOnlyList<AgentPermissionActionDescriptor> Actions() => (IReadOnlyList<AgentPermissionActionDescriptor>)SurfaceCall(permissions, "ListActions")!;
        AgentPermissionEvaluation Evaluate(string action, string boundary, Guid? id = null)
            => (AgentPermissionEvaluation)SurfaceCall(permissions, "Evaluate", id, new AgentPermissionRequest(action, boundary, "Fixture permission"))!;
    }
}
