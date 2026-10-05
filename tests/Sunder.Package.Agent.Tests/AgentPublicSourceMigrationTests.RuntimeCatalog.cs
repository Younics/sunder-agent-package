using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyRuntimeCatalogAsync(Assembly assembly, SunderRpcContractDescriptor descriptor)
    {
        using var package = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var generated = await CreateCanonicalRuntime(assembly, package.Context, catalog);
        await using var lifetime = generated.Lifetime;
        var services = generated.Services;
        var sessions = services.GetRequiredService<AgentSessionService>();
        var profiles = services.GetRequiredService<AgentProfileService>();
        var workspaces = services.GetRequiredService<AgentWorkspaceService>();
        var domain = services.GetRequiredService<AgentRuntimeCatalog>();
        var workspace = workspaces.CreateWorkspace("Catalog workspace 漢字");
        var secondWorkspace = workspaces.CreateWorkspace("Other workspace");
        var profile = await profiles.CreateProfileAsync("Catalog profile");
        profiles.SaveProfile(profile.ProfileId, "Catalog profile 漢字", "", "Instructions", "provider.one", "model.one", null, null,
            [new("tools", "capability.one", null)], "loop.one", "source.one", "{malformed", "{opaque-settings");
        sessions.CreateSession("Other session", workspaceId: secondWorkspace.WorkspaceId);
        var parent = sessions.CreateSession("Parent session", workspaceId: workspace.WorkspaceId);
        var session = sessions.CreateSession("Catalog session", parentSessionId: parent.SessionId, rootSessionId: parent.SessionId,
            parentRunId: Guid.NewGuid(), parentRunRevision: 7, parentToolCallId: "parent.call", taskId: "task.one",
            profileId: profile.ProfileId, behaviorLoopId: "loop.one", agentKind: "subagent", workspaceId: workspace.WorkspaceId);
        var first = sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "First message 漢字");
        var call = sessions.AppendToolCallTurn(session.SessionId, AgentMessageRole.Assistant, "call.one", "tool.one", "{opaque-arguments");
        var result = sessions.AppendToolResultTurn(session.SessionId, "call.one", "tool.one", "{opaque-arguments", "Output 漢字",
            "Summary", "{opaque-structured", "[opaque-sources", true, true, "tool.failed", "backend.one", "{opaque-presentation");
        var last = sessions.AppendTextTurn(session.SessionId, AgentMessageRole.Assistant, "Last message");
        sessions.SaveCheckpoint(session.SessionId, 7, AgentRunStatus.Completed, "Run completed");
        sessions.SaveSessionContextCheckpoint(session.SessionId, first.TurnId, call.TurnId, 2, "Working summary", "{opaque-details");
        Assert.Null(domain.GetWorkingSummary(session.SessionId)); // Unanchored legacy snapshots are not active context.
        var store = services.GetRequiredService<AgentLocalStore>();
        var reserved = store.ReserveRun(session.SessionId, profile.ProfileId, "Active request");
        var started = store.TryStartRun(reserved.Key, reserved.Epoch, "Active request", [], null, "running")!;
        var snapshot = store.ReadSessionContinuitySnapshot(session.SessionId, started.Transition.Run.Key.RunId, started.Transition.Run.Key.RunRevision)!;
        Assert.NotNull(store.TrySaveAnchoredSessionContextCheckpoint(new(
            snapshot.SessionId, snapshot.TranscriptEpoch, snapshot.ActiveContextCheckpointId, snapshot.ActiveContextGeneration,
            snapshot.SourceRun, snapshot.SourceRunEpoch, first.TurnId, call.TurnId, 2, call.CreatedAtUtc, call.ContentRevision,
            AgentSessionContextCheckpointKind.Deterministic, "Working summary", "{opaque-details", "test-catalog-v1")));
        Assert.NotNull(domain.GetWorkingSummary(session.SessionId));
        Assert.NotNull(((IAgentTranscriptCatalog)domain).GetTranscriptToolDetail(new(session.SessionId, ItemId: result.Items[0].ItemId)));

        await using var fixture = await CreateCatalogFixture(assembly, descriptor, domain);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        await Compare("list-sessions", "ListSessionsAsync", new { });
        await Compare("list-sessions-for-profile", "ListSessionsForProfileAsync", new { value = profile.ProfileId.ToUpperInvariant() });
        await Compare("list-sessions-for-workspace", "ListSessionsForWorkspaceAsync", new { value = workspace.WorkspaceId });
        var currentSession = await Compare("get-session", "GetSessionAsync", new { sessionId = session.SessionId });
        Assert.Equal("subagent", currentSession.GetProperty("agentKind").GetString());
        Assert.Equal(7, currentSession.GetProperty("parentRunRevision").GetInt64());
        await Compare("list-workspaces", "ListWorkspacesAsync", new { });
        await Compare("get-workspace", "GetWorkspaceAsync", new { value = workspace.WorkspaceId });
        await Compare("get-session-profile", "GetSessionProfileAsync", new { sessionId = session.SessionId });
        await Compare("get-working-summary", "GetWorkingSummaryAsync", new { sessionId = session.SessionId });
        await Compare("get-session-context-checkpoint", "GetSessionContextCheckpointAsync", new { sessionId = session.SessionId });
        await Compare("get-latest-checkpoint", "GetLatestCheckpointAsync", new { sessionId = session.SessionId });
        var recent = await Compare("list-recent-turns", "ListRecentTurnsAsync", new { sessionId = session.SessionId, limit = 1000 });
        Assert.Equal(5, recent.GetArrayLength());
        var headers = await Compare("list-recent-transcript-headers", "ListRecentTranscriptHeadersAsync", new { sessionId = session.SessionId, limit = 1000 });
        Assert.Contains(headers.EnumerateArray().SelectMany(turn => turn.GetProperty("items").EnumerateArray()), item =>
            item.GetProperty("isToolHeaderProjection").GetBoolean() && !item.TryGetProperty("argumentsJson", out _));
        foreach (var (route, method, turn) in new[]
        {
            ("list-turns-before", "ListTurnsBeforeAsync", last), ("list-turns-after", "ListTurnsAfterAsync", first),
            ("list-transcript-headers-before", "ListTranscriptHeadersBeforeAsync", last),
            ("list-transcript-headers-after", "ListTranscriptHeadersAfterAsync", first),
        })
        {
            var page = await Compare(route, method, new
            {
                sessionId = session.SessionId, createdAtUtc = turn.CreatedAtUtc.ToString("O"),
                turnId = turn.TurnId, limit = 2,
            });
            Assert.Equal(2, page.GetArrayLength());
            Assert.DoesNotContain(page.EnumerateArray(), value => value.GetProperty("turnId").GetGuid() == turn.TurnId);
        }
        var detail = await Compare("get-transcript-tool-detail", "GetTranscriptToolDetailAsync", new
        { sessionId = session.SessionId, callId = "call.one", itemId = result.Items[0].ItemId });
        Assert.Equal("Output 漢字", detail.GetProperty("outputText").GetString());
        Assert.True(detail.GetProperty("isError").GetBoolean());
        Assert.Equal("failed", detail.GetProperty("status").GetString());
        await Compare("list-profiles", "ListProfilesAsync", new { });
        await Compare("get-profile", "GetProfileAsync", new { value = profile.ProfileId });
        var binding = await Compare("get-session-model-binding", "GetSessionModelBindingAsync", new { sessionId = session.SessionId, capabilityKind = AgentModelCapabilityKinds.Chat });
        Assert.Equal("{opaque-settings", binding.GetProperty("settingsJson").GetString());
        await Compare("get-model-binding", "GetModelBindingAsync", new { profileId = profile.ProfileId, capabilityKind = AgentModelCapabilityKinds.Chat });
        Assert.Equal(21, covered.Count);
        Assert.Equal(covered.Order(StringComparer.Ordinal), descriptor.Services.Single().Methods.Where(method => method.MethodId != "watch").Select(method => method.MethodId).Order(StringComparer.Ordinal));

        foreach (var (route, method) in new[]
        {
            ("get-session", "GetSessionAsync"), ("get-session-profile", "GetSessionProfileAsync"),
            ("get-working-summary", "GetWorkingSummaryAsync"), ("get-session-context-checkpoint", "GetSessionContextCheckpointAsync"),
            ("get-latest-checkpoint", "GetLatestCheckpointAsync"), ("get-transcript-tool-detail", "GetTranscriptToolDetailAsync"),
        }) Assert.Equal(JsonValueKind.Null, (await Compare(route, method, new { sessionId = Guid.NewGuid() })).ValueKind);
        foreach (var (route, method) in new[] { ("get-workspace", "GetWorkspaceAsync"), ("get-profile", "GetProfileAsync") })
            Assert.Equal(JsonValueKind.Null, (await Compare(route, method, new { value = "missing" })).ValueKind);
        Assert.Equal(JsonValueKind.Null, (await Compare("get-model-binding", "GetModelBindingAsync", new { profileId = profile.ProfileId, capabilityKind = "unknown" })).ValueKind);
        foreach (var (route, method) in new[] { ("list-sessions-for-profile", "ListSessionsForProfileAsync"), ("list-sessions-for-workspace", "ListSessionsForWorkspaceAsync") })
            Assert.Equal(0, (await Compare(route, method, new { value = " " })).GetArrayLength());

        using (var cancellation = new CancellationTokenSource())
        {
            await using var events = CatalogEvents(fixture.Client, cancellation.Token).GetAsyncEnumerator();
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("reset", events.Current.GetProperty("kind").GetString());
            var added = sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "Live event");
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("turn", events.Current.GetProperty("kind").GetString());
            Assert.Equal(added.TurnId, events.Current.GetProperty("turn").GetProperty("turnId").GetGuid());
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("session", events.Current.GetProperty("kind").GetString());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }

        await VerifyCatalogBoundariesAsync(assembly, descriptor, domain, result);

        async Task<JsonElement> Compare(string route, string method, object request)
        {
            covered.Add(route);
            var json = JsonSerializer.SerializeToElement(request);
            var expected = await CatalogDomainResult(domain, route, json);
            var actual = await InvokeGeneratedClient(fixture.Client, method, json);
            Assert.True(JsonElement.DeepEquals(expected, actual), $"{route}: {expected} != {actual}");
            return actual;
        }
    }

    private static async Task VerifyCatalogBoundariesAsync(Assembly assembly, SunderRpcContractDescriptor descriptor,
        IAgentRuntimeCatalog domain, AgentTurnRecord turn)
    {
        // This wrapper intentionally has no internal transcript interface: retain the legacy fallback.
        var probe = new RuntimeCatalogProbe(domain);
        await using var fixture = await CreateCatalogFixture(assembly, descriptor, probe);
        var request = JsonSerializer.SerializeToElement(new { sessionId = turn.SessionId, limit = 1000 });
        var full = await InvokeGeneratedClient(fixture.Client, "ListRecentTurnsAsync", request);
        Assert.True(JsonElement.DeepEquals(full, await InvokeGeneratedClient(fixture.Client, "ListRecentTranscriptHeadersAsync", request)));
        foreach (var (fullMethod, headerMethod) in new[] { ("ListTurnsBeforeAsync", "ListTranscriptHeadersBeforeAsync"), ("ListTurnsAfterAsync", "ListTranscriptHeadersAfterAsync") })
        {
            var boundary = JsonSerializer.SerializeToElement(new { sessionId = turn.SessionId, limit = 1000, createdAtUtc = turn.CreatedAtUtc.ToString("O"), turnId = turn.TurnId });
            Assert.True(JsonElement.DeepEquals(await InvokeGeneratedClient(fixture.Client, fullMethod, boundary), await InvokeGeneratedClient(fixture.Client, headerMethod, boundary)));
        }
        Assert.Equal(JsonValueKind.Null, (await InvokeGeneratedClient(fixture.Client, "GetTranscriptToolDetailAsync",
            JsonSerializer.SerializeToElement(new { sessionId = turn.SessionId, itemId = turn.Items[0].ItemId }))).ValueKind);
        foreach (var limit in new[] { 0, 1001 })
        {
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "ListRecentTurnsAsync",
                JsonSerializer.SerializeToElement(new { sessionId = turn.SessionId, limit })));
            Assert.Equal(SunderRpcErrorKind.Validation, error.Error.Kind);
        }
        var imprecise = JsonSerializer.SerializeToElement(new { sessionId = turn.SessionId, limit = 1, turnId = turn.TurnId, createdAtUtc = "2026-09-18T12:00:00.123456789Z" });
        var precisionError = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "ListTurnsBeforeAsync", imprecise));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, precisionError.Error.Kind);
        Assert.Equal("agent.rpc.timestamp-not-representable", Assert.IsType<SunderRpcException>(fixture.State.LastInvocationFailure).Error.Code);
        probe.Fail = true;
        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "ListSessionsAsync", EmptyRequest()));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        Assert.DoesNotContain("private catalog storage detail", failure.Message, StringComparison.Ordinal);
        probe.Fail = false;
        probe.BlockWorkspaces = true;
        foreach (var (method, value) in new[] { ("ListWorkspacesAsync", EmptyRequest()), ("GetWorkspaceAsync", JsonSerializer.SerializeToElement(new { value = "workspace" })) })
        {
            using var cancellation = new CancellationTokenSource();
            probe.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = InvokeGeneratedClient(fixture.Client, method, value, cancellation.Token);
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        await VerifyCatalogWatchAsync(fixture, probe, turn);
    }

    private static async Task VerifyCatalogWatchAsync(CatalogFixture fixture, RuntimeCatalogProbe probe, AgentTurnRecord turn)
    {
        using var cancellation = new CancellationTokenSource();
        await using (var events = CatalogEvents(fixture.Client, cancellation.Token).GetAsyncEnumerator())
        await using (var peer = fixture.Rpc.SubscribeAsync(fixture.Source, "runtime-catalog", "watch", EmptyRequest(), cancellationToken: cancellation.Token).GetAsyncEnumerator())
        {
            await SameEvent("reset", 1);
            Assert.Equal(8, probe.SubscriberCount);
            probe.RaiseSession(turn.SessionId);
            await SameEvent("session", 2);
            probe.RaiseProfile("profile.漢字");
            await SameEvent("profile", 3);
            probe.RaiseTurn(turn with
            {
                RunId = Guid.NewGuid(), RunRevision = 11, IsStreaming = true, ContentRevision = 12,
                CreatedAtUtc = new DateTimeOffset(2026, 9, 18, 12, 34, 56, TimeSpan.FromHours(2)).AddTicks(1234567),
                Items = [turn.Items[0] with
                {
                    ToolExecutionId = Guid.NewGuid(), ToolExecutionStatus = AgentToolExecutionStatus.Ambiguous,
                    ToolOwnerPackageId = "package.owner", ToolSchemaId = "schema.one", ToolSchemaVersion = "1.2.3",
                    ToolDetailRevision = 13, ToolHasDetails = false, ToolHeaderHint = "Hint 漢字", ToolErrorSummary = "",
                }],
            });
            await SameEvent("turn", 4);
            probe.RaiseReset();
            await SameEvent("reset", 5);
            for (var index = 0; index < 300; index++) probe.RaiseSession(Guid.NewGuid());
            await SameEvent("reset", 50);
            for (var sequence = 51; sequence <= 305; sequence++) await SameEvent("session", sequence);
            var pending = events.MoveNextAsync().AsTask();
            Assert.False(pending.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            async Task SameEvent(string kind, long sequence)
            {
                Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(await peer.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(JsonElement.DeepEquals(peer.Current, events.Current), $"Independent subscriptions diverged: {peer.Current} != {events.Current}");
                Assert.Equal(kind, events.Current.GetProperty("kind").GetString());
                Assert.Equal(sequence, events.Current.GetProperty("sequence").GetInt64());
            }
        }
        Assert.Equal(0, probe.SubscriberCount);
        await using (var early = CatalogEvents(fixture.Client, default).GetAsyncEnumerator())
        {
            Assert.True(await early.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(4, probe.SubscriberCount);
        }
        Assert.Equal(0, probe.SubscriberCount);
        await using (var fault = CatalogEvents(fixture.Client, default).GetAsyncEnumerator())
        {
            Assert.True(await fault.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            // Conversion belongs to stream consumption; invalid output cannot throw on the persistence notification thread.
            probe.RaiseTurn(turn with { Role = (AgentMessageRole)999 });
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => fault.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, error.Error.Kind);
        }
        Assert.Equal(0, probe.SubscriberCount);
    }

    private static async Task<CatalogFixture> CreateCatalogFixture(Assembly assembly, SunderRpcContractDescriptor descriptor, IAgentRuntimeCatalog catalog)
    {
        var serviceType = assembly.GetType("Sunder.Package.Agent.Rpc.Public.RuntimeCatalogService", true)!;
        var implementation = Activator.CreateInstance(serviceType, catalog)!;
        var registration = Assert.IsType<SunderWorkerProviderRegistration>(serviceType.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(implementation, null));
        Assert.Equal("agent.runtime.catalog", registration.ProviderId);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.memory.semantic");
        state.Add("sunder.package.agent", descriptor, implementation, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        var providers = (await rpc.DiscoverAsync(descriptor.ContractId)).Providers;
        var scope = await rpc.CreateCallScopeAsync();
        var client = Activator.CreateInstance(assembly.GetType("Sunder.Generated.Rpc.RuntimeCatalog.RuntimeCatalogClient", true)!, scope,
            providers.Single(provider => provider.ProviderId == registration.ProviderId).Endpoint)!;
        return new(state, rpc, scope, client, providers.Single(provider => provider.ProviderId == registration.ProviderId).Endpoint);
    }

    private sealed class CatalogFixture(RegressionTestRpcState state, RegressionTestRpcClient rpc, ISunderRpcCallScope scope,
        object client, SunderRpcEndpointReference source) : IAsyncDisposable
    {
        public RegressionTestRpcState State { get; } = state;
        public RegressionTestRpcClient Rpc { get; } = rpc;
        public object Client { get; } = client;
        public SunderRpcEndpointReference Source { get; } = source;
        public async ValueTask DisposeAsync() { await scope.DisposeAsync(); State.Dispose(); }
    }

    private static IAsyncEnumerable<JsonElement> CatalogEvents(object client, CancellationToken token)
    {
        var method = client.GetType().GetMethod("WatchAsync")!;
        var request = JsonSerializer.Deserialize("{}", method.GetParameters()[0].ParameterType, ExecutionJson);
        var stream = method.Invoke(client, [request, token])!;
        return (IAsyncEnumerable<JsonElement>)typeof(AgentPublicSourceMigrationTests).GetMethod(nameof(ProjectGeneratedEvents), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(method.ReturnType.GetGenericArguments()[0]).Invoke(null, [stream, token])!;
    }

    private static async IAsyncEnumerable<JsonElement> ProjectGeneratedEvents<T>(object stream, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var item in ((IAsyncEnumerable<T>)stream).WithCancellation(token))
            yield return JsonSerializer.SerializeToElement(item, ExecutionJson);
    }
}
