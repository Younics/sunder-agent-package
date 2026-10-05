extern alias AgentApp;

using System.Reflection;
using System.Collections;
using System.Runtime.Loader;
using System.Text.Json;
using AgentApp::Sunder.Package.Agent.Contracts.Contracts;
using AgentApp::Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static Assembly LoadAgentEditorOwner(string worker, AssemblyLoadContext load)
    {
        using var source = File.OpenRead(Path.Combine(SingleProjectRoleOutput(Path.Combine(Path.GetDirectoryName(worker)!, "Worker"), "app"), "Sunder.Package.Agent.App.dll"));
        return load.LoadFromStream(source);
    }
    private static IAgentWorkspaceEditorContributor CreateAgentWorkspaceEditor(Assembly app, ISunderRpcClient rpc, SunderRpcProviderSnapshot selected)
        => new AppWorkspaceEditorProbe(Activator.CreateInstance(app.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedWorkspaceEditorClient", true)!, rpc, selected)!);

    internal static async Task VerifyAgentWorkspaceEditorConsumer(Assembly app)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        var peer = new WorkspaceEditorPeer();
        var handler = Add();
        var context = new AgentWorkspaceEditorContext(AgentRoleBoundary.As<AgentWorkspaceRecord>(AgentPublicSourceMigrationTests.CreatePromptRequest().Workspace!), "local", "configuration.one");
        var observed = new FilesRpcObserver(rpc, "save-section");
        var selected = Assert.Single(state.Discover(AgentRpcContractIds.WorkspaceEditor).Providers);
        var client = CreateAgentWorkspaceEditor(app, observed, selected);
        await using var lifetime = (IAsyncDisposable)client;
        var bindings = app.GetType("Sunder.Package.Agent.Runtime.Rpc.AgentWorkspaceEditorBindings", true)!;
        Func<IAgentWorkspaceEditorContributor, AgentWorkspaceEditorContext, CancellationToken, ValueTask<bool>> canEdit =
            (editor, value, token) => ((AppWorkspaceEditorProbe)editor).CanEditAsync(value, token);
        Assert.Equal(peer.ContributorId, client.ContributorId);
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
        observed.InvalidateAfterInvoke = true;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.SaveSectionAsync(context, request).AsTask())).Error.Kind);
        observed.InvalidateAfterInvoke = false; observed.Transform = null;
        await state.RetireAsync(handler);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.GetSectionsAsync(context).AsTask())).Error.Kind);
        handler = Add();
        var next = CreateAgentWorkspaceEditor(app, observed, Assert.Single(state.Discover(AgentRpcContractIds.WorkspaceEditor).Providers));
        await using var nextLifetime = (IAsyncDisposable)next;
        Assert.Single(await next.GetSectionsAsync(context));
        handler.BlockMethod = "get-sections"; handler.Gate = new();
        var stopping = next.GetSectionsAsync(context).AsTask();
        await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = nextLifetime.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping); await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => next.ContributorId).Error.Kind);
        handler.BlockMethod = null;
        Assert.Equal(0, observed.UnscopedLookups); Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));

        // The complete artifact includes the actual UI containment/snapshot layer;
        // the smaller owner-client fixture intentionally has no Avalonia view models.
        if (app.GetType("Sunder.Package.Agent.PackageViews.AgentWorkspaceEditorInvocation") is { } invocation)
        {
            var catalogType = app.GetType("Sunder.Package.Agent.Protocol.AgentRpcCatalog", true)!;
            using var catalog = (IDisposable)Activator.CreateInstance(catalogType, rpc)!;
            var service = bindings.GetProperty("Service", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var contributor = app.GetType("Sunder.Package.Agent.Contracts.Contracts.IAgentWorkspaceEditorContributor", true)!;
            var sections = app.GetType("Sunder.Package.Agent.Contracts.Models.AgentEditorSection", true)!;
            var discover = catalogType.GetMethods().Single(method => method.Name == "GetServiceReferences"
                && method.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == service.GetType().GetGenericTypeDefinition()).MakeGenericMethod(contributor);
            var reference = Assert.Single(((IEnumerable)discover.Invoke(catalog, [service])!).Cast<object>());
            var snapshot = invocation.GetMethod("GetSectionsSnapshotAsync")!;
            var localContext = JsonSerializer.SerializeToElement(context, MemoryProviderJson).Deserialize(snapshot.GetParameters()[1].ParameterType, MemoryProviderJson)!;
            var read = typeof(AgentSourceArtifactMigrationTests).GetMethod(nameof(CreateRoleEditorRead), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(contributor, sections).Invoke(null, [snapshot, localContext])!;
            var invoke = invocation.GetMethod("InvokeAsync")!.MakeGenericMethod(typeof(IReadOnlyList<>).MakeGenericType(sections));
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
                var pending = invoke.Invoke(null, [reference, catalog, operation, CancellationToken.None, read])!;
                var task = (Task)pending.GetType().GetMethod("AsTask")!.Invoke(pending, null)!;
                await task.WaitAsync(TimeSpan.FromSeconds(5)); return task.GetType().GetProperty("Result")!.GetValue(task)!;
            }
        }
        SurfaceHandler Add()
        {
            return (SurfaceHandler)AddGeneratedPeer(state, "example.editor", AgentRpcContractIds.WorkspaceEditor,
                peer, "fixture.editor", static handler => new SurfaceHandler(handler));
        }
    }

    private static Func<TEditor, CancellationToken, ValueTask<IReadOnlyList<TSection>>> CreateRoleEditorRead<TEditor, TSection>(MethodInfo snapshot, object context)
        => (editor, token) => (ValueTask<IReadOnlyList<TSection>>)snapshot.Invoke(null, [editor, context, token])!;

    private sealed class AppWorkspaceEditorProbe(object client) : IAgentWorkspaceEditorContributor, IAsyncDisposable
    {
        private readonly RoleLocalValue _values = new(client);
        public string ContributorId => _values.Property<string>("ContributorId");
        public bool CanEdit(AgentWorkspaceEditorContext context) => _values.Invoke<bool>("CanEdit", context);
        internal ValueTask<bool> CanEditAsync(AgentWorkspaceEditorContext context, CancellationToken token)
            => _values.InvokeAsync<bool>("CanEditAsync", context, token);
        public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(AgentWorkspaceEditorContext context, CancellationToken cancellationToken = default)
            => _values.InvokeAsync<IReadOnlyList<AgentEditorSection>>("GetSectionsAsync", context, cancellationToken);
        public ValueTask<AgentEditorSaveResult> SaveSectionAsync(AgentWorkspaceEditorContext context, AgentEditorSaveRequest request, CancellationToken cancellationToken = default)
            => _values.InvokeAsync<AgentEditorSaveResult>("SaveSectionAsync", context, request, cancellationToken);
        public ValueTask DisposeAsync() { _values.Dispose(); return ((IAsyncDisposable)client).DisposeAsync(); }
    }

    private sealed class WorkspaceEditorPeer : IAgentWorkspaceEditorContributor
    {
        internal bool Applicable = true;
        internal AgentWorkspaceEditorContext? Context;
        internal AgentEditorSaveRequest? Saved;
        internal IReadOnlyList<AgentEditorSection> Sections = [new("settings", "Paths 漢字", null,
            [new AgentEditorField("paths", "Paths", AgentEditorFieldKind.PathList, Items: [new("path.one", "/execution", false) { SecondaryValue = "/host" }])
            { UseSecondaryFolderPicker = true, Actions = [new("refresh", "Refresh", AgentEditorActionKind.RefreshField, Parameters: new Dictionary<string, string?> { ["reset"] = null, ["field"] = "paths" })] }])];
        public string ContributorId => "fixture.editor";
        public bool CanEdit(AgentWorkspaceEditorContext context) { Context = context; return Applicable; }
        public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(AgentWorkspaceEditorContext context, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Context = context; return ValueTask.FromResult(Sections); }
        public ValueTask<AgentEditorSaveResult> SaveSectionAsync(AgentWorkspaceEditorContext context, AgentEditorSaveRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Context = context; Saved = request; return ValueTask.FromResult(new AgentEditorSaveResult(false, "Review the path")); }
    }
}
