extern alias AgentApp;

using System.Reflection;
using System.Collections;
using System.Runtime.Loader;
using System.Text.Json;
using AgentApp::Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;
using AppServices = AgentApp::Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static Assembly LoadAgentEditorOwner(string worker, AssemblyLoadContext load)
    {
        using var source = File.OpenRead(Path.Combine(SingleProjectRoleOutput(Path.Combine(Path.GetDirectoryName(worker)!, "Worker"), "app"), "Sunder.Package.Agent.App.dll"));
        return load.LoadFromStream(source);
    }
    private static AppWorkspaceEditorProbe CreateAgentWorkspaceEditor(Assembly app, ISunderRpcClient rpc, SunderRpcProviderSnapshot selected)
    {
        var receiver = new GeneratedServiceTestScope(rpc);
        try
        {
            var catalog = receiver.Providers(app.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Services.IWorkspaceEditorService", true)!);
            var snapshot = AgentRoleBoundary.Await(AgentRoleBoundary.Call(catalog, "ListAsync", CancellationToken.None)!).GetAwaiter().GetResult()!;
            var providers = (IEnumerable)snapshot.GetType().GetProperty("Providers")!.GetValue(snapshot)!;
            var entry = Assert.Single(providers.Cast<object>(), provider =>
                Equals(provider.GetType().GetProperty("ActivationId")!.GetValue(provider), selected.ActivationId)
                && Equals(provider.GetType().GetProperty("ProviderId")!.GetValue(provider), selected.ProviderId));
            return new AppWorkspaceEditorProbe(AgentRoleBoundary.As<AppServices.IWorkspaceEditorService>(
                entry.GetType().GetProperty("Service")!.GetValue(entry)!), receiver);
        }
        catch
        {
            receiver.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    internal static async Task VerifyAgentWorkspaceEditorConsumer(Assembly app)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "younics.ai.agent");
        var peer = new WorkspaceEditorPeer();
        var handler = Add();
        Assert.Empty(handler.Calls);
        var context = new AgentWorkspaceEditorContext(AgentRoleBoundary.As<AgentWorkspaceRecord>(AgentPublicSourceMigrationTests.CreatePromptRequest().Workspace!), "local", "configuration.one");
        var observed = new FilesRpcObserver(rpc, "save-section");
        var selected = Assert.Single(state.Discover(RegressionTestBindings.WorkspaceEditors).Providers);
        var client = CreateAgentWorkspaceEditor(app, observed, selected);
        await using var lifetime = (IAsyncDisposable)client;
        Func<AppWorkspaceEditorProbe, AgentWorkspaceEditorContext, CancellationToken, ValueTask<bool>> canEdit =
            static (editor, value, token) => editor.CanEditAsync(value, token);
        Assert.Equal(peer.ContributorId, (await client.DescribeAsync()).Id);
        Assert.True(await canEdit(client, context, default));
        peer.Applicable = false; Assert.False(await canEdit(client, context, default)); peer.Applicable = true;
        var field = Assert.Single(Assert.Single(await client.GetSectionsAsync(context)).Fields);
        Assert.Equal(AgentEditorFieldKind.PathList, field.Kind);
        Assert.Equal(AgentEditorActionKind.RefreshField, Assert.Single(field.Actions!).Kind);
        Assert.Null(field.Actions![0].Parameters!["reset"]); Assert.Equal("paths", field.Actions[0].Parameters!["field"]);
        Assert.Equal("/host", Assert.Single(field.Items!).SecondaryValue); Assert.True(field.UseSecondaryFolderPicker);
        var request = new AgentEditorSaveRequest("settings", new Dictionary<string, AgentEditorFieldValue>
        { ["paths"] = new(null, [new("path.one", "/execution", true) { SecondaryValue = "/host" }]) });
        Assert.False((await client.SaveSectionAsync(context, request)).Success);
        Assert.Equal(context.ConfigurationId, peer.Context!.ConfigurationId);
        Assert.Equal("/host", Assert.Single(peer.Saved!.Fields["paths"].Items!).SecondaryValue);
        Assert.True(Assert.Single(peer.Saved.Fields["paths"].Items!).IsDefault);

        handler.BlockMethod = "can-edit"; handler.Gate = new();
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = canEdit(client, context, cancelled.Token).AsTask();
            await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await handler.Gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        handler.BlockMethod = null;
        observed.Transform = value => value with { SessionGeneration = value.SessionGeneration + 1 };
        var before = handler.Calls.Count;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.SaveSectionAsync(context, request).AsTask())).Error.Kind);
        Assert.Equal(before, handler.Calls.Count); observed.Transform = null;
        await using var invalidatedClient = CreateAgentWorkspaceEditor(app, observed, selected);
        observed.InvalidateAfterInvoke = true;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => invalidatedClient.SaveSectionAsync(context, request).AsTask())).Error.Kind);
        Assert.Equal(before + 1, handler.Calls.Count);
        observed.InvalidateAfterInvoke = false; observed.Transform = null;
        await invalidatedClient.DisposeAsync();
        var peerCount = handler.Calls.Count;
        await state.RetireAsync(handler);
        var retired = await Assert.ThrowsAsync<SunderRpcException>(() => client.GetSectionsAsync(context).AsTask());
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, retired.Error.Kind);
        Assert.Equal(peerCount, handler.Calls.Count);
        handler = Add();
        var next = CreateAgentWorkspaceEditor(app, observed, Assert.Single(state.Discover(RegressionTestBindings.WorkspaceEditors).Providers));
        await using var nextLifetime = (IAsyncDisposable)next;
        Assert.Single(await next.GetSectionsAsync(context));
        handler.BlockMethod = "get-sections"; handler.Gate = new();
        var stopping = next.GetSectionsAsync(context).AsTask();
        await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = nextLifetime.DisposeAsync().AsTask();
        var inFlightRetired = await Assert.ThrowsAsync<SunderRpcException>(() => stopping);
        Assert.Equal(SunderRpcErrorKind.Unavailable, inFlightRetired.Error.Kind);
        Assert.Equal("rpc.binding.retired", inFlightRetired.Error.Code);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, inFlightRetired.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, inFlightRetired.Error.RetrySafety);
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => next.DescribeAsync().AsTask());
        handler.BlockMethod = null;
        await lifetime.DisposeAsync();

        // The complete artifact includes the actual UI containment/snapshot layer;
        // the smaller owner-client fixture intentionally has no Avalonia view models.
        if (app.GetType("Sunder.Package.Agent.PackageViews.AgentWorkspaceEditorInvocation") is { } invocation)
        {
            await using var receiver = new GeneratedServiceTestScope(observed);
            var catalog = receiver.Providers(app.GetType("Sunder.Generated.Packages.Younics_Ai_Agent_Services.IWorkspaceEditorService", true)!);
            var snapshot = await AgentRoleBoundary.Await(AgentRoleBoundary.Call(catalog, "ListAsync", CancellationToken.None)!);
            var reference = Assert.Single(((IEnumerable)snapshot!.GetType().GetProperty("Providers")!.GetValue(snapshot)!).Cast<object>(),
                provider => Equals(provider.GetType().GetProperty("ProviderId")!.GetValue(provider), peer.ContributorId));
            var service = AgentRoleBoundary.As<AppServices.IWorkspaceEditorService>(reference.GetType().GetProperty("Service")!.GetValue(reference)!);
            Assert.Equal(peer.ContributorId, (await service.DescribeAsync()).Id);
            var invoke = invocation.GetMethod("ReadSectionsAsync")!;
            var localContext = JsonSerializer.SerializeToElement(context, MemoryProviderJson).Deserialize(invoke.GetParameters()[1].ParameterType, MemoryProviderJson)!;
            var operation = Enum.Parse(invoke.GetParameters()[2].ParameterType, "Discovery");
            var successful = await Read();
            Assert.True((bool)successful.GetType().GetProperty("Success")!.GetValue(successful)!);
            handler.FailMethod = "get-sections";
            var failed = await Read(); var failure = failed.GetType().GetProperty("Failure")!.GetValue(failed)!;
            Assert.Equal("Runtime", failure.GetType().GetProperty("Kind")!.GetValue(failure)!.ToString());
            Assert.DoesNotContain("private-surface-failure", failure.GetType().GetProperty("Message")!.GetValue(failure)!.ToString()!, StringComparison.Ordinal);
            handler.FailMethod = null;
            peer.Sections = [peer.Sections[0], peer.Sections[0]];
            failed = await Read(); failure = failed.GetType().GetProperty("Failure")!.GetValue(failed)!;
            Assert.Equal("OwnerFaulted", failure.GetType().GetProperty("Kind")!.GetValue(failure)!.ToString());

            async Task<object> Read()
            {
                var pending = invoke.Invoke(null, [reference, localContext, operation, CancellationToken.None])!;
                var task = (Task)pending.GetType().GetMethod("AsTask")!.Invoke(pending, null)!;
                await task.WaitAsync(TimeSpan.FromSeconds(5)); return task.GetType().GetProperty("Result")!.GetValue(task)!;
            }
        }
        observed.AssertScopedOperations(); Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));
        SurfaceHandler Add()
        {
            return (SurfaceHandler)AddGeneratedPeer(state, "example.editor", RegressionTestBindings.WorkspaceEditors,
                peer, "fixture.editor", static handler => new SurfaceHandler(handler));
        }
    }

    private sealed class AppWorkspaceEditorProbe(AppServices.IWorkspaceEditorService service, GeneratedServiceTestScope receiver) : IAsyncDisposable
    {
        public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken token = default) => service.DescribeAsync(token);
        public ValueTask<bool> CanEditAsync(AgentWorkspaceEditorContext context, CancellationToken token = default) => service.CanEditAsync(context, token);
        public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(AgentWorkspaceEditorContext context, CancellationToken cancellationToken = default) => service.GetSectionsAsync(context, cancellationToken);
        public ValueTask<AgentEditorSaveResult> SaveSectionAsync(AgentWorkspaceEditorContext context, AgentEditorSaveRequest request, CancellationToken cancellationToken = default) => service.SaveSectionAsync(context, request, cancellationToken);
        public ValueTask DisposeAsync() => receiver.DisposeAsync();
    }

    private sealed class WorkspaceEditorPeer : AppServices.IWorkspaceEditorService
    {
        internal bool Applicable = true;
        internal AgentWorkspaceEditorContext? Context;
        internal AgentEditorSaveRequest? Saved;
        internal IReadOnlyList<AgentEditorSection> Sections = [new("settings", "Paths 漢字", null,
            [new AgentEditorField("paths", "Paths", AgentEditorFieldKind.PathList, Items: [new("path.one", "/execution", false) { SecondaryValue = "/host" }])
            { UseSecondaryFolderPicker = true, Actions = [new("refresh", "Refresh", AgentEditorActionKind.RefreshField, Parameters: new Dictionary<string, string?> { ["reset"] = null, ["field"] = "paths" })] }])];
        public string ContributorId => "fixture.editor";
        public bool CanEdit(AgentWorkspaceEditorContext context) { Context = context; return Applicable; }
        public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentServiceIdentity(ContributorId, "Fixture workspace editor"));
        public ValueTask<bool> CanEditAsync(AgentWorkspaceEditorContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(CanEdit(context));
        public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(AgentWorkspaceEditorContext context, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Context = context; return ValueTask.FromResult(Sections); }
        public ValueTask<AgentEditorSaveResult> SaveSectionAsync(AgentWorkspaceEditorContext context, AgentEditorSaveRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Context = context; Saved = request; return ValueTask.FromResult(new AgentEditorSaveResult(false, "Review the path")); }
    }
}
