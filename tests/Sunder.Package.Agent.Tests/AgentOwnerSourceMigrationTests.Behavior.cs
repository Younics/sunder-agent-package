using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentOwnerSourceMigrationTests
{
    private static readonly JsonSerializerOptions OwnerJson = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new SunderRpcJsonConverterFactory() } };

    internal static async Task VerifyBehavior(Assembly workerAssembly, Assembly appAssembly, SunderRpcContractDescriptor descriptor,
        Func<IAgentOwnerRuntimeProvider, object>? adaptProvider = null)
    {
        using var package = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var generated = await AgentPublicSourceMigrationTests.CreateCanonicalRuntime(typeof(AgentOwnerRuntimeProvider).Assembly, package.Context, catalog);
        await using var lifetime = generated.Lifetime;
        var services = generated.Services;
        var probe = new OwnerProviderProbe(services.GetRequiredService<AgentOwnerRuntimeProvider>());
        var type = workerAssembly.GetType("Sunder.Package.Agent.Rpc.Owner.OwnerRuntimeService", true)!;
        var implementation = AgentRoleBoundary.Create(type, adaptProvider?.Invoke(probe) ?? probe);
        var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(implementation, null));
        Assert.Equal("agent.owner.runtime", registration.ProviderId);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        state.Add("sunder.package.agent", descriptor, implementation, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers);
        var app = new AppClient(appAssembly, rpc);
        var negotiated = await app.Invoke("transport-profile", new { });
        Assert.False(negotiated.GetProperty("runtimeReported").GetBoolean());
        Assert.Equal(8 * 1024 * 1024, negotiated.GetProperty("requestValueBytes").GetInt64());
        Assert.Single(probe.Requests.OfType<AgentTransportProfileRequest>());

        var profile = (await app.Invoke("profile-command", new AgentProfileCommand(AgentProfileCommandKind.Create, DisplayName: "Owner profile 漢字")))
            .GetProperty("profile").Deserialize<AgentProfileRecord>(OwnerJson)!;
        var workspace = (await app.Invoke("workspace-command", new AgentWorkspaceCommand(AgentWorkspaceCommandKind.Create, DisplayName: "Owner workspace")))
            .GetProperty("workspace").Deserialize<AgentWorkspaceRecord>(OwnerJson)!;
        var sessionResult = await app.Invoke("session-command", new AgentSessionCommand(AgentSessionCommandKind.Create,
            Title: "Owner session", ProfileId: profile.ProfileId, WorkspaceId: workspace.WorkspaceId));
        var session = sessionResult.GetProperty("session").GetProperty("session").Deserialize<AgentSessionRecord>(OwnerJson)!;
        var sessions = services.GetRequiredService<AgentSessionService>();
        var turn = sessions.AppendTextTurn(session.SessionId, AgentMessageRole.User, "Persisted owner transcript");
        sessions.AppendToolCallTurn(session.SessionId, AgentMessageRole.Assistant, "call.one", "tool.one", "{opaque");
        var tool = sessions.AppendToolResultTurn(session.SessionId, "call.one", "tool.one", "{opaque", "Owner tool output", "Result", null, null, false, false, null, null);
        await app.Invoke("profile-command", new AgentProfileCommand(AgentProfileCommandKind.Save, ProfileId: profile.ProfileId,
            DisplayName: "Updated profile", Instructions: "User instruction", BehaviorLoopSettingsJson: "{opaque"));
        Assert.Equal("Updated profile", services.GetRequiredService<AgentProfileService>().GetProfile(profile.ProfileId)!.DisplayName);
        await app.Invoke("workspace-command", new AgentWorkspaceCommand(AgentWorkspaceCommandKind.Save, WorkspaceId: workspace.WorkspaceId,
            DisplayName: "Updated workspace", Paths: [], Documents: []));
        await app.Invoke("session-command", new AgentSessionCommand(AgentSessionCommandKind.Update, Session: session with { Title = "Updated session" }));

        var readCases = new (string Route, object Request)[]
        {
            ("chat-snapshot", new AgentChatSnapshotRequest(10, profile.ProfileId, workspace.WorkspaceId, session.SessionId, false)),
            ("dashboard", new AgentDashboardRequest()),
            ("transcript-page", new AgentTranscriptPageRequest(session.SessionId, AgentTranscriptPageDirection.Recent, 10)),
            ("catalog", new AgentCatalogRequest()),
            ("run-status", new AgentRunCommandStatusRequest(session.SessionId, Guid.NewGuid())),
            ("permission-command", new AgentPermissionCommand(AgentPermissionCommandKind.Read, session.SessionId)),
            ("history-search", new HistorySearchRequest("owner", IncludeChildSessions: false, Limit: 10)),
            ("history-state", new HistorySearchStateRequest(Limit: 10, IncludeAdvancedFilters: false)),
            ("transcript-around", new AgentTranscriptAroundTurnRequest(session.SessionId, turn.TurnId)),
            ("transcript-tool-detail", new AgentTranscriptToolDetailRequest(session.SessionId, ItemId: tool.Items[0].ItemId)),
        };
        foreach (var (route, request) in readCases)
        {
            var expected = await OwnerDomainResult(probe, route, request);
            var actual = await app.Invoke(route, request);
            Assert.True(JsonElement.DeepEquals(expected, actual), $"{route}: {expected} != {actual}");
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(request, OwnerJson), JsonSerializer.SerializeToElement(probe.Requests[^1], probe.Requests[^1].GetType(), OwnerJson)));
        }
        Assert.Single(probe.Requests.OfType<AgentTransportProfileRequest>());
        await app.Invoke("permission-command", new AgentPermissionCommand(AgentPermissionCommandKind.SetUnrestricted, session.SessionId, false));
        await app.Invoke("history-command", new HistorySearchCommand(HistorySearchCommandKind.ConfigureSemantic, SemanticEnabled: false));
        var userTurnId = Guid.NewGuid();
        await app.Invoke("run-command", new AgentRunCommand(AgentRunCommandKind.Start, session.SessionId, profile.ProfileId,
            "Queued through generated owner", workspace.WorkspaceId, [], ApproveForSession: false, UserTurnId: userTurnId));
        Assert.Equal(AgentRunCommandStatus.Committed, (await app.Invoke("run-status", new AgentRunCommandStatusRequest(session.SessionId, userTurnId)))
            .GetProperty("status").Deserialize<AgentRunCommandStatus>(OwnerJson));
        Assert.NotNull(sessions.GetTurn(userTurnId));
        await app.Invoke("run-command", new AgentRunCommand(AgentRunCommandKind.Stop, session.SessionId));

        await VerifyAttachments(app, services, state);
        await VerifyStreams(app, services, probe, session.SessionId);
        Assert.Equal(descriptor.Services.Single().Methods.Select(method => method.RequestSchemaReference.Split('/')[^1]).Order(StringComparer.Ordinal),
            probe.Requests.Select(request => request.GetType().Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        await VerifyFailures(app, appAssembly, rpc, probe, state, implementation, descriptor, registration);
    }

    private static async Task VerifyAttachments(AppClient app, IServiceProvider services, RegressionTestRpcState state)
    {
        var bytes = Encoding.UTF8.GetBytes("Owner attachment 漢字 " + new string('x', 128 * 1024));
        var handleJson = await app.Upload(bytes, default);
        var handle = JsonSerializer.Deserialize<AgentAttachmentUploadHandle>(handleJson, OwnerJson)!;
        var transfers = services.GetRequiredService<AgentAttachmentTransferService>();
        var adoption = transfers.BeginAdoption([handle]);
        try { Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(adoption.Uploads).SourcePath)); }
        finally { transfers.ReleaseAdoption(adoption); }
        var stored = await services.GetRequiredService<AgentAttachmentService>().StoreAttachmentAsync(Guid.NewGuid(), new("stored.txt", "text/plain", bytes));
        var metadata = JsonSerializer.Serialize(stored.Metadata, OwnerJson);
        Assert.Equal(bytes, await app.Download(metadata, default));
        await app.Discard(handleJson, default);
        Assert.Throws<AgentAttachmentTransferOperationException>(() => transfers.BeginAdoption([handle]));
        state.FailNextResponseContentRegistration(new IOException("private content registration failure"));
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => app.Download(metadata, default));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, error.Error.Kind);
        Assert.DoesNotContain("private content", error.Message, StringComparison.Ordinal);
    }

    private static async Task VerifyStreams(AppClient app, IServiceProvider services, OwnerProviderProbe probe, Guid sessionId)
    {
        using var cancellation = new CancellationTokenSource();
        var revision = services.GetRequiredService<AgentRuntimeChangeHub>().Revision;
        await using (var events = app.Watch("changes", JsonSerializer.Serialize(new AgentChangeSubscription(revision, SupportsTurnMutations: true), OwnerJson), cancellation.Token).GetAsyncEnumerator())
        {
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            using var connected = JsonDocument.Parse(events.Current);
            Assert.Equal((int)AgentRuntimeChangeKind.Connected, connected.RootElement.GetProperty("kind").GetInt32());
            services.GetRequiredService<AgentSessionService>().AppendTextTurn(sessionId, AgentMessageRole.User, "Live owner change");
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            using var changed = JsonDocument.Parse(events.Current);
            Assert.Equal((int)AgentRuntimeChangeKind.TurnMutation, changed.RootElement.GetProperty("kind").GetInt32());
            Assert.True(changed.RootElement.TryGetProperty("turnMutation", out _));
            Assert.False(changed.RootElement.TryGetProperty("turn", out _));
            cancellation.Cancel();
        }
        var hub = services.GetRequiredService<AgentRuntimeChangeHub>();
        await using (var events = app.Watch("changes", JsonSerializer.Serialize(new AgentChangeSubscription(hub.Revision), OwnerJson), default).GetAsyncEnumerator())
        {
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            for (var index = 0; index < 70; index++) hub.NotifyPermissionChanged(sessionId);
            for (var index = 0; index < 64; index++)
            {
                Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                using var change = JsonDocument.Parse(events.Current);
                Assert.Equal((int)AgentRuntimeChangeKind.Permission, change.RootElement.GetProperty("kind").GetInt32());
            }
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            using var reset = JsonDocument.Parse(events.Current);
            Assert.Equal((int)AgentRuntimeChangeKind.ResnapshotRequired, reset.RootElement.GetProperty("kind").GetInt32());
            Assert.Equal(hub.Revision, reset.RootElement.GetProperty("revision").GetInt64());
            Assert.False(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        probe.StreamFailure = new AgentOwnerOperationException("Expected stream failure");
        await using (var events = app.Watch("history-status", JsonSerializer.Serialize(new HistorySearchStatusSubscription(), OwnerJson), default).GetAsyncEnumerator())
        {
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("agent.owner.operation-failed", error.Error.Code);
            Assert.Equal("Expected stream failure", error.Error.Message);
        }
        probe.StreamFailure = null;
    }

    private static async Task VerifyFailures(AppClient app, Assembly appAssembly, RegressionTestRpcClient rpc, OwnerProviderProbe probe,
        RegressionTestRpcState state, object implementation, SunderRpcContractDescriptor descriptor, SunderWorkerProviderRegistration registration)
    {
        var invalid = await Assert.ThrowsAsync<SunderRpcException>(() => app.Invoke("chat-snapshot", new AgentChatSnapshotRequest(0)));
        Assert.Equal(SunderRpcErrorKind.Validation, invalid.Error.Kind);
        foreach (var message in new[] { " ", new string('x', 3000) })
        {
            probe.Failure = new AgentOwnerOperationException(message);
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => app.Invoke("dashboard", new AgentDashboardRequest()));
            Assert.Equal("agent.owner.operation-failed", error.Error.Code);
            Assert.Equal(message == " " ? "The requested Agent operation failed." : message[..2048], error.Error.Message);
        }
        probe.Failure = new IOException("private owner failure");
        var unexpected = await Assert.ThrowsAsync<SunderRpcException>(() => app.Invoke("dashboard", new AgentDashboardRequest()));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, unexpected.Error.Kind);
        Assert.DoesNotContain("private owner", unexpected.Message, StringComparison.Ordinal);
        probe.Failure = null;
        probe.OverrideProfile = new(0, 1, 1, 1, false);
        await Assert.ThrowsAsync<InvalidDataException>(() => new AppClient(appAssembly, rpc).Invoke("transport-profile", new { }));
        probe.OverrideProfile = new(9 * 1024 * 1024, 8 * 1024 * 1024, 8 * 1024 * 1024, 64 * 1024 * 1024, false);
        await Assert.ThrowsAsync<InvalidDataException>(() => new AppClient(appAssembly, rpc).Invoke("transport-profile", new { }));
        probe.OverrideProfile = null;
        var before = probe.Requests.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Invoke("profile-command", new AgentProfileCommand(AgentProfileCommandKind.Create, DisplayName: new string('\u0001', 2 * 1024 * 1024))));
        Assert.Equal(before, probe.Requests.Count);
        using (var cancellation = new CancellationTokenSource())
        {
            probe.Block = true;
            var pending = app.Invoke("dashboard", new AgentDashboardRequest(), cancellation.Token);
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            probe.Block = false;
        }
        probe.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.Block = true;
        var retiring = app.Invoke("dashboard", new AgentDashboardRequest());
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(implementation).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => retiring.WaitAsync(TimeSpan.FromSeconds(5)))).Error.Kind);
        probe.Block = false;
        state.Add("unrelated.package", descriptor, new object(), registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        Assert.Equal(SunderRpcErrorKind.Unavailable, (await Assert.ThrowsAsync<SunderRpcException>(() => app.Invoke("dashboard", new AgentDashboardRequest()))).Error.Kind);
        state.Add("sunder.package.agent", descriptor, implementation, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        var negotiations = probe.Requests.OfType<AgentTransportProfileRequest>().Count();
        await app.Invoke("dashboard", new AgentDashboardRequest());
        Assert.Equal(negotiations + 1, probe.Requests.OfType<AgentTransportProfileRequest>().Count());
        state.Add("sunder.package.agent", descriptor, new object(), registration.Handler, Noop.Instance, "alternate.owner", "2.0.0");
        Assert.Equal("rpc.provider-ambiguous", (await Assert.ThrowsAsync<SunderRpcException>(() => app.Invoke("dashboard", new AgentDashboardRequest()))).Error.Code);
    }

    private sealed class Noop : IDisposable { internal static Noop Instance { get; } = new(); public void Dispose() { } }

    private static async Task<JsonElement> OwnerDomainResult(IAgentOwnerRuntimeProvider provider, string route, object request)
    {
        object result = route switch
        {
            "chat-snapshot" => await provider.GetChatSnapshotAsync((AgentChatSnapshotRequest)request, default),
            "dashboard" => await provider.GetDashboardAsync((AgentDashboardRequest)request, default),
            "transcript-page" => await provider.GetTranscriptPageAsync((AgentTranscriptPageRequest)request, default),
            "catalog" => await provider.GetCatalogAsync((AgentCatalogRequest)request, default),
            "run-status" => await provider.GetRunStatusAsync((AgentRunCommandStatusRequest)request, default),
            "permission-command" => await provider.ExecutePermissionCommandAsync((AgentPermissionCommand)request, default),
            "history-search" => await provider.SearchHistoryAsync((HistorySearchRequest)request, default),
            "history-state" => await provider.GetHistoryStateAsync((HistorySearchStateRequest)request, default),
            "transcript-around" => await provider.GetTranscriptAroundAsync((AgentTranscriptAroundTurnRequest)request, default),
            "transcript-tool-detail" => await provider.GetTranscriptToolDetailAsync((AgentTranscriptToolDetailRequest)request, default),
            _ => throw new InvalidOperationException("Uncovered owner domain scenario: " + route),
        };
        return JsonSerializer.SerializeToElement(result, result.GetType(), OwnerJson);
    }
}
