using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
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
        using var domain = new RuntimeCatalogProbe(services.GetRequiredService<AgentRuntimeCatalog>(), sessions, profiles);
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
        Assert.Null(await domain.GetWorkingSummaryAsync(session.SessionId)); // Unanchored legacy snapshots are not active context.
        var store = services.GetRequiredService<AgentLocalStore>();
        var reserved = store.ReserveRun(session.SessionId, profile.ProfileId, "Active request");
        var started = store.TryStartRun(reserved.Key, reserved.Epoch, "Active request", [], null, "running")!;
        var snapshot = store.ReadSessionContinuitySnapshot(session.SessionId, started.Transition.Run.Key.RunId, started.Transition.Run.Key.RunRevision)!;
        Assert.NotNull(store.TrySaveAnchoredSessionContextCheckpoint(new(
            snapshot.SessionId, snapshot.TranscriptEpoch, snapshot.ActiveContextCheckpointId, snapshot.ActiveContextGeneration,
            snapshot.SourceRun, snapshot.SourceRunEpoch, first.TurnId, call.TurnId, 2, call.CreatedAtUtc, call.ContentRevision,
            AgentSessionContextCheckpointKind.Deterministic, "Working summary", "{opaque-details", "test-catalog-v1")));
        Assert.NotNull(await domain.GetWorkingSummaryAsync(session.SessionId));
        Assert.NotNull(await domain.GetTranscriptToolDetailAsync(new(session.SessionId, ItemId: result.Items[0].ItemId)));

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
            var actual = await InvokeCatalogAsync(fixture.Client, method, json);
            Assert.True(JsonElement.DeepEquals(expected, actual), $"{route}: {expected} != {actual}");
            return actual;
        }
    }

    private static async Task VerifyCatalogBoundariesAsync(Assembly assembly, SunderRpcContractDescriptor descriptor,
        RuntimeCatalogProbe domain, AgentTurnRecord turn)
    {
        // A controlled provider can return full transcript values without header
        // projections; retain the boundary assertions through its generated codec.
        using var probe = new RuntimeCatalogProbe(domain);
        await using var fixture = await CreateCatalogFixture(assembly, descriptor, probe);
        var request = JsonSerializer.SerializeToElement(new { sessionId = turn.SessionId, limit = 1000 });
        var full = await InvokeCatalogAsync(fixture.Client, "ListRecentTurnsAsync", request);
        var validRecentTurns = fixture.State.LastInvocationRequest;
        Assert.True(JsonElement.DeepEquals(full, await InvokeCatalogAsync(fixture.Client, "ListRecentTranscriptHeadersAsync", request)));
        foreach (var (fullMethod, headerMethod) in new[] { ("ListTurnsBeforeAsync", "ListTranscriptHeadersBeforeAsync"), ("ListTurnsAfterAsync", "ListTranscriptHeadersAfterAsync") })
        {
            var boundary = JsonSerializer.SerializeToElement(new { sessionId = turn.SessionId, limit = 1000, createdAtUtc = turn.CreatedAtUtc.ToString("O"), turnId = turn.TurnId });
            Assert.True(JsonElement.DeepEquals(await InvokeCatalogAsync(fixture.Client, fullMethod, boundary), await InvokeCatalogAsync(fixture.Client, headerMethod, boundary)));
        }
        var validBoundary = fixture.State.LastInvocationRequest;
        Assert.Equal(JsonValueKind.Null, (await InvokeCatalogAsync(fixture.Client, "GetTranscriptToolDetailAsync",
            JsonSerializer.SerializeToElement(new { sessionId = turn.SessionId, itemId = turn.Items[0].ItemId }))).ValueKind);
        foreach (var limit in new[] { 0, 1001 })
        {
            var calls = probe.ProviderCalls;
            var attempts = fixture.State.InvocationAttemptCount;
            await Assert.ThrowsAsync<JsonException>(() => InvokeCatalogAsync(fixture.Client, "ListRecentTurnsAsync",
                JsonSerializer.SerializeToElement(new { sessionId = turn.SessionId, limit })));
            Assert.Equal(attempts, fixture.State.InvocationAttemptCount);
            Assert.Equal(calls, probe.ProviderCalls);
            var invalidWire = JsonNode.Parse(validRecentTurns.GetRawText())!.AsObject();
            invalidWire["limit"] = limit;
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Rpc.InvokeAsync(fixture.Source,
                "runtime-catalog", "list-recent-turns", JsonSerializer.SerializeToElement(invalidWire)).AsTask());
            Assert.Equal(SunderRpcErrorKind.Validation, error.Error.Kind);
            Assert.Equal(attempts + 1, fixture.State.InvocationAttemptCount);
            Assert.Equal(calls, probe.ProviderCalls);
        }
        var imprecise = JsonNode.Parse(validBoundary.GetRawText())!.AsObject();
        imprecise["createdAtUtc"] = "2026-09-18T12:00:00.123456789Z";
        // The imported fixture carrier preserves arbitrary offset precision. Test
        // representability at the actual canonical provider's DateTimeOffset
        // decoder, not by rounding the input in this fixture's argument adapter.
        using (var preciseState = new RegressionTestRpcState(workerStyleProviderBoundary: true))
        {
            var preciseRpc = new RegressionTestRpcClient(preciseState, "sunder.package.agent.memory.semantic");
            await using var preciseReceiver = new GeneratedServiceTestScope(preciseRpc);
            using var sources = new RegressionTestSourceProviders();
            var registration = sources.Create(descriptor.ContractId, domain.CanonicalOwner, preciseReceiver);
            var endpoint = preciseState.AddGenerated("sunder.package.agent", domain.CanonicalOwner, registration,
                EmptyDisposable.Instance, registration.ProviderId).Endpoint;
            var precisionError = await Assert.ThrowsAsync<SunderRpcException>(() => preciseRpc.InvokeAsync(endpoint,
                "runtime-catalog", "list-turns-before", JsonSerializer.SerializeToElement(imprecise)).AsTask());
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, precisionError.Error.Kind);
            Assert.Equal("rpc.provider.handler-fault", precisionError.Error.Code);
            Assert.IsType<JsonException>(preciseState.LastInvocationFailure);
        }
        probe.Fail = true;
        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeCatalogAsync(fixture.Client, "ListSessionsAsync", EmptyRequest()));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        Assert.DoesNotContain("private catalog storage detail", failure.Message, StringComparison.Ordinal);
        probe.Fail = false;
        probe.BlockWorkspaces = true;
        foreach (var (method, value) in new[] { ("ListWorkspacesAsync", EmptyRequest()), ("GetWorkspaceAsync", JsonSerializer.SerializeToElement(new { value = "workspace" })) })
        {
            using var cancellation = new CancellationTokenSource();
            probe.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = InvokeCatalogAsync(fixture.Client, method, value, cancellation.Token);
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

    private static async Task<JsonElement> InvokeCatalogAsync(AgentServices.IRuntimeCatalogService client, string method,
        JsonElement request, CancellationToken cancellationToken = default)
    {
        var sessionId = request.TryGetProperty("sessionId", out var session) ? session.GetGuid() : default;
        var value = request.TryGetProperty("value", out var rawValue) ? rawValue.GetString()! : null!;
        var limit = request.TryGetProperty("limit", out var rawLimit) ? rawLimit.GetInt32() : 0;
        var capabilityKind = request.TryGetProperty("capabilityKind", out var rawCapability) ? rawCapability.GetString()! : null!;
        var profileId = request.TryGetProperty("profileId", out var rawProfile) ? rawProfile.GetString()! : null!;
        var createdAtUtc = request.TryGetProperty("createdAtUtc", out var rawCreatedAt) ? rawCreatedAt.GetDateTimeOffset() : default;
        var turnId = request.TryGetProperty("turnId", out var rawTurnId) ? rawTurnId.GetGuid() : default;
        object? result = method switch
        {
            "ListSessionsAsync" => await client.ListSessionsAsync(cancellationToken),
            "ListSessionsForProfileAsync" => await client.ListSessionsForProfileAsync(value, cancellationToken),
            "ListSessionsForWorkspaceAsync" => await client.ListSessionsForWorkspaceAsync(value, cancellationToken),
            "GetSessionAsync" => await client.GetSessionAsync(sessionId, cancellationToken),
            "ListWorkspacesAsync" => await client.ListWorkspacesAsync(cancellationToken),
            "GetWorkspaceAsync" => await client.GetWorkspaceAsync(value, cancellationToken),
            "GetSessionProfileAsync" => await client.GetSessionProfileAsync(sessionId, cancellationToken),
            "GetWorkingSummaryAsync" => await client.GetWorkingSummaryAsync(sessionId, cancellationToken),
            "GetSessionContextCheckpointAsync" => await client.GetSessionContextCheckpointAsync(sessionId, cancellationToken),
            "GetLatestCheckpointAsync" => await client.GetLatestCheckpointAsync(sessionId, cancellationToken),
            "ListRecentTurnsAsync" => await client.ListRecentTurnsAsync(sessionId, limit, cancellationToken),
            "ListTurnsBeforeAsync" => await client.ListTurnsBeforeAsync(sessionId, createdAtUtc, turnId, limit, cancellationToken),
            "ListTurnsAfterAsync" => await client.ListTurnsAfterAsync(sessionId, createdAtUtc, turnId, limit, cancellationToken),
            "ListRecentTranscriptHeadersAsync" => await client.ListRecentTranscriptHeadersAsync(sessionId, limit, cancellationToken),
            "ListTranscriptHeadersBeforeAsync" => await client.ListTranscriptHeadersBeforeAsync(sessionId, createdAtUtc, turnId, limit, cancellationToken),
            "ListTranscriptHeadersAfterAsync" => await client.ListTranscriptHeadersAfterAsync(sessionId, createdAtUtc, turnId, limit, cancellationToken),
            "GetTranscriptToolDetailAsync" => await client.GetTranscriptToolDetailAsync(
                JsonSerializer.Deserialize<AgentTranscriptToolDetailRequest>(request.GetRawText(), ExecutionJson)!, cancellationToken),
            "ListProfilesAsync" => await client.ListProfilesAsync(cancellationToken),
            "GetProfileAsync" => await client.GetProfileAsync(value, cancellationToken),
            "GetSessionModelBindingAsync" => await client.GetSessionModelBindingAsync(sessionId, capabilityKind, cancellationToken),
            "GetModelBindingAsync" => await client.GetModelBindingAsync(profileId, capabilityKind, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown runtime catalog method."),
        };
        return RunJson(result);
    }

    private static async Task<CatalogFixture> CreateCatalogFixture(Assembly assembly, SunderRpcContractDescriptor descriptor, RuntimeCatalogProbe catalog)
    {
        var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent.memory.semantic");
        var receiver = new GeneratedServiceTestScope(rpc);
        catalog.ProjectionReceiver = receiver;
        var fixtureRoot = Directory.CreateTempSubdirectory("sunder-agent-runtime-catalog-").FullName;
        var fixtureLoad = new AssemblyLoadContext("agent-runtime-catalog", isCollectible: true);
        object implementation;
        try
        {
            implementation = await CreateGeneratedExtensionProviderAsync(fixtureRoot, fixtureLoad, "RuntimeCatalog", catalog.Invoke);
        }
        catch
        {
            fixtureLoad.Unload();
            Directory.Delete(fixtureRoot, recursive: true);
            await receiver.DisposeAsync();
            state.Dispose();
            throw;
        }
        var registration = receiver.Register(implementation, descriptor.ContractId);
        Assert.Equal("fixture.regression.runtime.catalog", registration.ProviderId);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        state.Add("sunder.package.agent", descriptor, implementation, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        var provider = Assert.Single((await receiver.Providers<AgentServices.IRuntimeCatalogService>().ListAsync()).Providers,
            value => value.ProviderId == registration.ProviderId);
        var endpoint = Assert.Single(state.Discover(descriptor.ContractId).Providers,
            value => value.ProviderId == registration.ProviderId).Endpoint;
        return new(state, rpc, receiver, fixtureLoad, fixtureRoot, provider.Service, endpoint);
    }

    private sealed class CatalogFixture(RegressionTestRpcState state, RegressionTestRpcClient rpc, GeneratedServiceTestScope receiver,
        AssemblyLoadContext fixtureLoad, string fixtureRoot, AgentServices.IRuntimeCatalogService client, SunderRpcEndpointReference source) : IAsyncDisposable
    {
        public RegressionTestRpcState State { get; } = state;
        public RegressionTestRpcClient Rpc { get; } = rpc;
        public AgentServices.IRuntimeCatalogService Client { get; } = client;
        public SunderRpcEndpointReference Source { get; } = source;
        public async ValueTask DisposeAsync()
        {
            await receiver.DisposeAsync();
            State.Dispose();
            fixtureLoad.Unload();
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static async IAsyncEnumerable<JsonElement> CatalogEvents(AgentServices.IRuntimeCatalogService client,
        [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var item in client.WatchAsync(token).WithCancellation(token))
        {
            object snapshot = item switch
            {
                AgentRuntimeCatalogResetEvent reset => new { kind = "reset", reset.Sequence },
                AgentSessionCatalogEvent session => new { kind = "session", session.Sequence, session.SessionId },
                AgentTurnCatalogEvent turn => new { kind = "turn", turn.Sequence, turn.SessionId, turn.Turn },
                AgentProfileCatalogEvent profile => new { kind = "profile", profile.Sequence, profile.ProfileId },
                _ => throw new InvalidOperationException("Unknown catalog event."),
            };
            var value = JsonSerializer.SerializeToNode(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
            })!;
            if (item is AgentTurnCatalogEvent turnEvent)
            {
                // Match the reviewed exact offset representation, including seven
                // tick digits; the general JSON serializer trims trailing zeros.
                value["turn"]!["createdAtUtc"] = turnEvent.Turn.CreatedAtUtc.ToString("O");
                value["turn"]!["updatedAtUtc"] = turnEvent.Turn.UpdatedAtUtc.ToString("O");
            }
            yield return JsonSerializer.SerializeToElement(value);
        }
    }
}
