using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static async Task VerifyAgentToolConsumers(Assembly owner, Assembly app)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        var peer = new AgentToolPeer();
        var descriptor = RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ToolSource);
        using var providerCatalog = AgentRpcCatalog.CreateDormant(rpc.ForCaller("example.tools"));
        var handler = new SurfaceHandler(new Sunder.Generated.Rpc.ToolSource.SunderAgentToolSourceRpcHandler(new RegressionTestToolProvider(peer, providerCatalog)));
        Add(handler);
        var selected = Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers);
        var observed = new FilesRpcObserver(rpc, "execute");
        var client = CreateAgentToolClient(owner, observed, selected);
        await using var lifetime = (IAsyncDisposable)client;
        var permission = (IAgentPermissionAwareToolSource)client;
        var preflight = (IAgentToolExecutionPreflightSource)client;
        var presentation = (IAgentToolPresentationResolver)client;
        var seed = AgentPublicSourceMigrationTests.CreatePromptRequest();
        var source = new AgentToolSourceContext(seed.Session.SessionId, seed.Profile, seed.Workspace, seed.ExecutionBinding)
        { ExecutionTargetConfigurationGeneration = "captured-generation" };
        var execution = new AgentToolExecutionContext(seed.Session.SessionId, seed.Profile!.ProfileId, seed.Workspace, seed.ExecutionBinding, true,
            seed.Run.RunId, seed.Run.Revision, Guid.NewGuid(), "call.tools")
        {
            TranscriptEpoch = 11, ApprovedResourceReferences = ["durable"], ApprovedResourceCapabilities = ["transient"],
            ExecutionTargetConfigurationGeneration = "captured-generation",
            ResourceOperation = new(Guid.NewGuid(), 7, "call.tools", "files.mutate", -1, "workspace-generation", "binding-generation",
                "example.tools", "example.target", "activation", 2, true),
        };
        var request = new AgentToolRequest("fixture.tool", "{opaque arguments 漢字");
        var display = new AgentToolPresentationRequest(request.ToolId, request.ArgumentsJson, "summary", "output", "{opaque structured", "[opaque sources", true, "fixture.error", "fixture.backend");
        Assert.Equal(peer.SourceId, client.SourceId); Assert.Equal(peer.DisplayName, client.DisplayName); Assert.Equal(peer.SourceKind, client.SourceKind);
        Assert.Single(handler.Calls, call => call.Method == "describe");
        var listed = Assert.Single(await client.ListToolsAsync(source)); Assert.Equal(peer.Tool, listed);
        Assert.Equal(seed.Profile.BehaviorLoopSettingsJson, peer.SourceContext!.Profile!.BehaviorLoopSettingsJson);
        Assert.Equal(source.ExecutionTargetConfigurationGeneration, peer.SourceContext.ExecutionTargetConfigurationGeneration);
        Assert.Equal(peer.Readiness, await client.GetReadinessAsync(request.ToolId, source));
        Assert.Equal(peer.Result, await preflight.PreflightExecutionAsync(execution, request));
        var granted = await permission.BuildPermissionRequestAsync(execution, request);
        Assert.Equal(peer.Permission!.ResourceCapabilities, granted!.ResourceCapabilities);
        Assert.Equal(peer.Permission.ResourceReferences, granted.ResourceReferences);
        Assert.Equal(execution.ResourceOperation, peer.ExecutionContext!.ResourceOperation);
        Assert.Equal(execution.ApprovedResourceCapabilities, peer.ExecutionContext.ApprovedResourceCapabilities);
        Assert.True(JsonElement.DeepEquals(RegressionTestRpcValues.ToolInvocation(execution, request),
            handler.Calls.Last(call => call.Method == "permission").Request));
        var dispatches = 0;
        var before = DateTimeOffset.UtcNow;
        Assert.Equal(peer.Result, await client.ExecuteAsync(execution with { DispatchStarted = () => dispatches++ }, request));
        Assert.Equal(0, dispatches); // Unary dispatch remains caller-owned.
        Assert.Contains(observed.Scopes, scope => scope.DeadlineUtc >= before.AddMinutes(34) && scope.DeadlineUtc <= DateTimeOffset.UtcNow.AddMinutes(36));
        Assert.Equal(peer.Presentation, presentation.ResolveToolPresentation(display)); Assert.Equal(display, peer.PresentationRequest);
        peer.Readiness = null; peer.Permission = null; peer.Preflight = null; peer.Presentation = null;
        Assert.Null(await client.GetReadinessAsync("missing", source)); Assert.Null(await permission.BuildPermissionRequestAsync(execution, request));
        Assert.Null(await preflight.PreflightExecutionAsync(execution, request)); Assert.Null(presentation.ResolveToolPresentation(display));
        foreach (var change in new Func<SunderRpcProviderSnapshot, SunderRpcProviderSnapshot>[]
        {
            value => value with { PackageId = "other.package" }, value => value with { PackageVersion = "2.0.1" },
            value => value with { ProviderId = "other.provider" }, value => value with { ContractId = "other.contract" },
            value => value with { ContractVersion = "1.0.1" }, value => value with { DescriptorSha256 = new string('c', 64) },
            value => value with { AbiSha256 = new string('d', 64) }, value => value with { ActivationId = Guid.NewGuid() },
            value => value with { ActivationEpoch = value.ActivationEpoch + 1 }, value => value with { SessionGeneration = value.SessionGeneration + 1 },
            value => value with { Endpoint = new("other-endpoint") }, value => value with { State = SunderRpcProviderState.Inactive },
        })
        {
            observed.Transform = change; var calls = handler.Calls.Count;
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.ExecuteAsync(execution, request).AsTask())).Error.Kind);
            Assert.Equal(calls, handler.Calls.Count);
        }
        observed.Transform = null; observed.InvalidateAfterInvoke = true;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.ExecuteAsync(execution, request).AsTask())).Error.Kind);
        observed.InvalidateAfterInvoke = false; observed.Transform = null;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var calls = handler.Calls.Count;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListToolsAsync(source, cancelled.Token).AsTask());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteAsync(execution, request, cancelled.Token).AsTask()); Assert.Equal(calls, handler.Calls.Count);
        }
        handler.BlockMethod = "permission"; handler.Gate = new();
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = permission.BuildPermissionRequestAsync(execution, request, cancelled.Token).AsTask();
            await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        handler.BlockMethod = "execute"; handler.Gate = new();
        var retiring = client.ExecuteAsync(execution, request).AsTask(); await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(handler).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => retiring)).Error.Kind);
        handler = new(new Sunder.Generated.Rpc.ToolSource.SunderAgentToolSourceRpcHandler(new RegressionTestToolProvider(peer, providerCatalog))); Add(handler);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.ListToolsAsync(source).AsTask())).Error.Kind);
        selected = Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers);
        var next = CreateAgentToolClient(owner, observed, selected); await using var nextLifetime = (IAsyncDisposable)next;
        Assert.Single(await next.ListToolsAsync(source));
        handler.BlockMethod = "execute"; handler.Gate = new();
        var stopping = next.ExecuteAsync(execution, request).AsTask(); await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = nextLifetime.DisposeAsync().AsTask(); Assert.Same(stopped, nextLifetime.DisposeAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping); await stopped.WaitAsync(TimeSpan.FromSeconds(5)); handler.Gate = null;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => next.SourceId).Error.Kind);
        Assert.Equal(0, observed.UnscopedLookups); Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));

        peer.Presentation = new("Generated App header", "Untrusted details", "Generated output");
        var appClient = Activator.CreateInstance(app.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedToolSourceClient", true)!, rpc, selected)!;
        await using var appLifetime = (IAsyncDisposable)appClient;
        Assert.Same(app, appClient.GetType().Assembly); Assert.NotSame(AgentRoleBoundary.Unwrap(client).GetType(), appClient.GetType());
        using var appValues = new RoleLocalValue(appClient);
        Assert.Equal(peer.Presentation, appValues.Invoke<AgentToolPresentation?>("ResolveToolPresentation", display));
        using var appCatalog = (IDisposable)app.GetType("Sunder.Package.Agent.Protocol.AgentRpcCatalog", true)!
            .GetMethod("CreateDormant", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [rpc.ForCaller("sunder.package.agent")])!;
        if (app.GetType("Sunder.Package.Agent.Services.AgentToolPresentationService") is { } presentationType)
        {
            var service = Activator.CreateInstance(presentationType, appCatalog)!;
            using var values = new RoleLocalValue(service);
            var item = seed.Turns[0].Items[0] with { ToolId = request.ToolId, ArgumentsJson = request.ArgumentsJson, TextContent = "fallback output" };
            Assert.Equal("Generated App header", values.Invoke<AgentToolPresentation>("Resolve", item).HeaderText);
            handler.FailMethod = "presentation";
            Assert.Equal("fallback output", values.Invoke<AgentToolPresentation>("Resolve", item).OutputText);
            handler.FailMethod = null;
        }
        await VerifyAgentToolPolicy(owner, state, peer);
        await VerifyAgentObservedTools(owner);
        void Add(SurfaceHandler value) => state.Add("example.tools", descriptor, value, value, Noop.Instance, "fixture.tools", "2.0.0");
    }

    private static IAgentToolSource CreateAgentToolClient(Assembly owner, ISunderRpcClient rpc, SunderRpcProviderSnapshot selected)
        => AgentRoleBoundary.As<IAgentToolSource>(Activator.CreateInstance(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedToolSourceClient", true)!, rpc, selected)!);

    private static async Task VerifyAgentToolPolicy(Assembly owner, RegressionTestRpcState state, AgentToolPeer peer)
    {
        using var package = RegressionTestPackageScope.Create();
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        await using var host = new AgentPromptHost(owner, package.Context, rpc);
        var profiles = host.Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentProfileService", true)!);
        SurfaceCall(profiles, "ImportProfile", host.Profile with { SelectableCapabilityAssignments = [new("tool", peer.Tool.ToolId, peer.SourceId)] });
        var catalog = host.Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentRuntimeCatalog", true)!);
        AddSurfaceProvider(state, "sunder.package.agent", SurfaceRegistration(Activator.CreateInstance(owner.GetType("Sunder.Package.Agent.Rpc.Public.RuntimeCatalogService", true)!, catalog)!));
        var tools = host.Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentToolService", true)!);
        peer.Tool = peer.Tool with { ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}" };
        peer.Permission = null;
        var permission = await (Task<AgentPermissionRequest?>)SurfaceCall(tools, "BuildPermissionRequestAsync", peer.Tool.ToolId, "{}",
            host.Session.SessionId, host.Profile.ProfileId, host.Workspace, host.RunId, 1L, null, "call.policy", CancellationToken.None, null)!;
        Assert.Equal("agent.tool.mutate", permission!.ActionId); Assert.Equal("provider-requested-mutation", permission.BoundaryId);
        var dispatches = 0;
        var result = await ExecuteAgentTool(tools, peer.Tool.ToolId, "{}", host, host.Profile.ProfileId, () => dispatches++);
        Assert.False(result.IsError, result.Content); Assert.Equal(1, dispatches);
    }

    private static Task<AgentToolResult> ExecuteAgentTool(object tools, string tool, string arguments, AgentPromptHost host, string profileId, Action dispatch,
        CancellationToken token = default)
    {
        var method = tools.GetType().GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var args = method.GetParameters().Select(parameter => parameter.Name switch
        {
            "toolId" => (object)tool, "argumentsJson" => arguments, "sessionId" => host.Session.SessionId, "profileId" => profileId,
            "workspace" => host.Workspace, "runId" => host.RunId, "runRevision" => 1L, "toolCallId" => "call.generated", "dispatchStarted" => dispatch,
            "cancellationToken" => token, _ => parameter.DefaultValue,
        }).ToArray();
        return AgentRoleBoundary.As<Task<AgentToolResult>>(AgentRoleBoundary.Call(tools, "ExecuteAsync", args)!).WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class AgentToolPeer : IAgentToolSource, IAgentPermissionAwareToolSource, IAgentToolExecutionPreflightSource, IAgentToolPresentationResolver
    {
        public string SourceId => "fixture.tools";
        public string DisplayName => "Fixture tools";
        public string SourceKind => "local";
        internal AgentToolDescriptor Tool { get; set; } = new("fixture.tool", "Fixture", "Static package description", false, true, "{opaque schema",
            Priority: AgentToolPriority.High) { ConcurrencyMode = AgentToolConcurrencyMode.ParallelSafe };
        internal AgentToolResult Result { get; } = new("fixture.tool", "Completed 漢字", "Output", "{opaque result", WasTruncated: true,
            BackendId: "fixture", PresentationPayloadJson: "{opaque presentation") { RequiresPromptContextRefresh = true };
        internal AgentToolReadiness? Readiness = new("fixture.tool", AgentToolReadinessStatus.Ready, "Ready");
        internal AgentPermissionRequest? Permission = new("files.mutate", "outside-configured-scope", "Mutation", ResourceReference: "durable", IsMutation: true)
        { ResourceReferences = ["durable"], ResourceCapabilities = ["transient"] };
        internal AgentToolResult? Preflight;
        internal AgentToolPresentation? Presentation = new("Header", "Details", "Output");
        internal AgentToolSourceContext? SourceContext;
        internal AgentToolExecutionContext? ExecutionContext;
        internal AgentToolPresentationRequest? PresentationRequest;
        internal AgentToolPeer() => Preflight = Result;
        public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); SourceContext = context; return ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>([Tool]); }
        public ValueTask<AgentToolReadiness?> GetReadinessAsync(string toolId, AgentToolSourceContext context, CancellationToken cancellationToken = default) => ValueTask.FromResult(Readiness);
        public ValueTask<AgentToolResult?> PreflightExecutionAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
        { ExecutionContext = context; return ValueTask.FromResult(Preflight); }
        public ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
        { ExecutionContext = context; return ValueTask.FromResult(Permission); }
        public ValueTask<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request, CancellationToken cancellationToken = default)
        { ExecutionContext = context; return ValueTask.FromResult(Result); }
        public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request) { PresentationRequest = request; return Presentation; }
    }
}
