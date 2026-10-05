extern alias AgentCore;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using AgentCore::Sunder.Package.Agent.Contracts.Models;
using AgentCore::Sunder.Package.Agent.HistorySearch;
using AgentCore::Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Xunit;
using AgentRuntimePayloadLimits = AgentCore::Sunder.Package.Agent.Runtime.AgentRuntimePayloadLimits;

namespace Sunder.Package.Agent.Tests;

extern alias SubagentsApp;
extern alias SubagentsWorker;
extern alias AgentApp;
using AgentApp::Sunder.Package.Agent.Runtime;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime;
using SubagentModels = SubagentsWorker::Sunder.Package.Agent.Contracts.Models;
using WorkerRuntime = Sunder.Package.Agent.Runtime;

public sealed class AgentRuntimeTransportLimitTests
{
    private const int Mebibyte = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly AgentTransportProfile TestProfile = new(
        1024 * 1024,
        1024 * 1024,
        1024 * 1024,
        32 * 1024 * 1024,
        RuntimeReported: true);
    private static int ResponsePayloadBytes => AgentRuntimePayloadLimits.GetResponsePayloadBytes(TestProfile);
    private static int EventPayloadBytes => AgentRuntimePayloadLimits.GetEventPayloadBytes(TestProfile);

    [Fact]
    public void AgentTransportLimits_UseStandardFallbackAndRetainPayloadReserve()
    {
        Assert.Equal(8 * 1024 * 1024, AgentRuntimePayloadLimits.StandardFallbackProfile.RequestValueBytes);
        Assert.Equal(8 * 1024 * 1024, AgentRuntimePayloadLimits.StandardFallbackProfile.ResponseValueBytes);
        Assert.Equal(8 * 1024 * 1024, AgentRuntimePayloadLimits.StandardFallbackProfile.EventValueBytes);
        Assert.Equal(64 * 1024 * 1024, AgentRuntimePayloadLimits.StandardFallbackProfile.ContentReferenceBytes);

        var budgets = new WorkerBudgetProbe();
        Assert.Equal(AgentRuntimePayloadLimits.StandardFallbackProfile, budgets.Current);
        var negotiated = budgets.Negotiate(new AgentTransportProfileRequest(
            TestProfile.RequestValueBytes,
            TestProfile.ResponseValueBytes,
            TestProfile.EventValueBytes,
            TestProfile.ContentReferenceBytes,
            RuntimeReported: true));

        var expected = TestProfile with { RuntimeReported = false };
        Assert.Equal(expected, negotiated);
        Assert.Equal(expected, budgets.Current);
        Assert.Equal(
            TestProfile.ResponseValueBytes - AgentRuntimePayloadLimits.EnvelopeReserveBytes,
            AgentRuntimePayloadLimits.GetResponsePayloadBytes(negotiated));
    }

    [Fact]
    public void RuntimeTransportBudgets_InitializeFromWorkerAndNegotiateComponentwise()
    {
        using var rpcState = new RegressionTestRpcState();
        var workerProfile = ResourceProfile(4 * Mebibyte, 128 * Mebibyte);
        var budgets = new WorkerBudgetProbe(new RegressionTestRpcClient(
            rpcState,
            "sunder.package.agent",
            workerProfile));

        Assert.Equal(
            new AgentTransportProfile(
                4 * Mebibyte,
                4 * Mebibyte,
                4 * Mebibyte,
                128 * Mebibyte,
                RuntimeReported: true),
            budgets.Current);

        var negotiated = budgets.Negotiate(new AgentTransportProfileRequest(
            2 * Mebibyte,
            8 * Mebibyte,
            Mebibyte,
            256 * Mebibyte,
            RuntimeReported: true));

        Assert.Equal(
            new AgentTransportProfile(
                2 * Mebibyte,
                4 * Mebibyte,
                Mebibyte,
                128 * Mebibyte,
                RuntimeReported: true),
            negotiated);
        Assert.Equal(negotiated, budgets.Current);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(8, 64)]
    [InlineData(16, 256)]
    [InlineData(32, 256)]
    public void RuntimeTransportBudgets_HandleMatchingStandardLargeAndCustomHighProfiles(
        int valueMebibytes,
        int contentMebibytes)
    {
        using var rpcState = new RegressionTestRpcState();
        var valueBytes = valueMebibytes * Mebibyte;
        var contentBytes = contentMebibytes * Mebibyte;
        var budgets = new WorkerBudgetProbe(new RegressionTestRpcClient(
            rpcState,
            "sunder.package.agent",
            ResourceProfile(valueBytes, contentBytes)));

        var negotiated = budgets.Negotiate(new AgentTransportProfileRequest(
            valueBytes,
            valueBytes,
            valueBytes,
            contentBytes,
            RuntimeReported: true));

        Assert.Equal(
            new AgentTransportProfile(
                valueBytes,
                valueBytes,
                valueBytes,
                contentBytes,
                RuntimeReported: true),
            negotiated);
    }

    [Theory]
    [InlineData(512 * 1024, 512 * 1024)]
    [InlineData(64 * 1024 * 1024, 512 * 1024 * 1024)]
    public void RuntimeTransportBudgets_AcceptRuntimeProfilesOutsideFormerAgentCeilings(
        int valueBytes,
        int contentBytes)
    {
        using var rpcState = new RegressionTestRpcState();
        var budgets = new WorkerBudgetProbe(new RegressionTestRpcClient(
            rpcState,
            "sunder.package.agent",
            ResourceProfile(valueBytes, contentBytes)));

        var negotiated = budgets.Negotiate(new AgentTransportProfileRequest(
            valueBytes,
            valueBytes,
            valueBytes,
            contentBytes,
            RuntimeReported: true));

        Assert.Equal(valueBytes, negotiated.RequestValueBytes);
        Assert.Equal(valueBytes, negotiated.ResponseValueBytes);
        Assert.Equal(valueBytes, negotiated.EventValueBytes);
        Assert.Equal(contentBytes, negotiated.ContentReferenceBytes);
    }

    [Fact]
    public void RuntimeTransportBudgets_UseFallbackForMissingSidesAndRequireBothReports()
    {
        var appOnly = new WorkerBudgetProbe().Negotiate(new AgentTransportProfileRequest(
            16 * Mebibyte,
            16 * Mebibyte,
            16 * Mebibyte,
            256 * Mebibyte,
            RuntimeReported: true));
        Assert.Equal(AgentRuntimePayloadLimits.StandardFallbackProfile, appOnly);

        using var rpcState = new RegressionTestRpcState();
        var workerOnlyBudgets = new WorkerBudgetProbe(new RegressionTestRpcClient(
            rpcState,
            "sunder.package.agent",
            ResourceProfile(2 * Mebibyte, 4 * Mebibyte)));
        var workerOnly = workerOnlyBudgets.Negotiate(new AgentTransportProfileRequest(
            AgentRuntimePayloadLimits.StandardFallbackProfile.RequestValueBytes,
            AgentRuntimePayloadLimits.StandardFallbackProfile.ResponseValueBytes,
            AgentRuntimePayloadLimits.StandardFallbackProfile.EventValueBytes,
            AgentRuntimePayloadLimits.StandardFallbackProfile.ContentReferenceBytes,
            RuntimeReported: false));
        Assert.Equal(
            new AgentTransportProfile(
                2 * Mebibyte,
                2 * Mebibyte,
                2 * Mebibyte,
                4 * Mebibyte,
                RuntimeReported: false),
            workerOnly);

        var neither = new WorkerBudgetProbe().Negotiate(new AgentTransportProfileRequest(
            AgentRuntimePayloadLimits.StandardFallbackProfile.RequestValueBytes,
            AgentRuntimePayloadLimits.StandardFallbackProfile.ResponseValueBytes,
            AgentRuntimePayloadLimits.StandardFallbackProfile.EventValueBytes,
            AgentRuntimePayloadLimits.StandardFallbackProfile.ContentReferenceBytes,
            RuntimeReported: false));
        Assert.Equal(AgentRuntimePayloadLimits.StandardFallbackProfile, neither);
    }

    [Fact]
    public void RuntimeTransportBudgets_RejectNonPositiveAppProposalDimensions()
    {
        var budgets = new WorkerBudgetProbe();
        var valid = new AgentTransportProfileRequest(
            1,
            1,
            1,
            1,
            RuntimeReported: false);

        Assert.Throws<WorkerRuntime.AgentOwnerOperationException>(() => budgets.Negotiate(valid with
        {
            RequestValueBytes = 0,
        }));
        Assert.Throws<WorkerRuntime.AgentOwnerOperationException>(() => budgets.Negotiate(valid with
        {
            ResponseValueBytes = -1,
        }));
        Assert.Throws<WorkerRuntime.AgentOwnerOperationException>(() => budgets.Negotiate(valid with
        {
            EventValueBytes = 0,
        }));
        Assert.Throws<WorkerRuntime.AgentOwnerOperationException>(() => budgets.Negotiate(valid with
        {
            ContentReferenceBytes = 0,
        }));
    }

    [Fact]
    public void GeneratedComposition_WiresWorkerRpcResourceProfileAndAllowsMissingProfile()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var rpcState = new RegressionTestRpcState();
        var workerProfile = ResourceProfile(16 * Mebibyte, 256 * Mebibyte);
        using var profiledProvider = RegressionTestRuntime.Create(scope.Context, new RegressionTestRpcClient(
            rpcState,
            "sunder.package.agent",
            workerProfile));

        Assert.Equal(
            new AgentTransportProfile(
                16 * Mebibyte,
                16 * Mebibyte,
                16 * Mebibyte,
                256 * Mebibyte,
                RuntimeReported: true),
            AgentRoleBoundary.As<AgentTransportProfile>(profiledProvider.GetRequiredService<WorkerRuntime.AgentRuntimeTransportBudgetState>().Current));

        using var fallbackProvider = RegressionTestRuntime.Create(scope.Context);
        Assert.Equal(
            AgentRuntimePayloadLimits.StandardFallbackProfile,
            AgentRoleBoundary.As<AgentTransportProfile>(fallbackProvider.GetRequiredService<WorkerRuntime.AgentRuntimeTransportBudgetState>().Current));
    }

    private sealed class WorkerBudgetProbe(ISunderRpcClient? rpc = null)
    {
        private readonly WorkerRuntime.AgentRuntimeTransportBudgetState _worker = new(rpc);
        internal AgentTransportProfile Current => AgentRoleBoundary.As<AgentTransportProfile>(_worker.Current);
        internal AgentTransportProfile Negotiate(AgentTransportProfileRequest proposal)
            => AgentRoleBoundary.As<AgentTransportProfile>(_worker.Negotiate(
                AgentRoleBoundary.As<WorkerRuntime.AgentTransportProfileRequest>(proposal)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnerRpc_ProposesSdkProfileOrStandardFallback(bool runtimeReported)
    {
        using var rpcState = new RegressionTestRpcState();
        var resourceProfile = runtimeReported
            ? ResourceProfile(12 * Mebibyte, 96 * Mebibyte)
            : null;
        var expected = runtimeReported
            ? new AgentTransportProfile(
                12 * Mebibyte,
                12 * Mebibyte,
                12 * Mebibyte,
                96 * Mebibyte,
                RuntimeReported: true)
            : AgentRuntimePayloadLimits.StandardFallbackProfile;
        var owner = CreateOwnerClient(rpcState, resourceProfile, expected, out var handler);

        var negotiated = await owner.GetTransportProfileAsync(CancellationToken.None);

        Assert.Equal(expected, negotiated);
        Assert.Equal(
            new AgentTransportProfileRequest(
                expected.RequestValueBytes,
                expected.ResponseValueBytes,
                expected.EventValueBytes,
                expected.ContentReferenceBytes,
                expected.RuntimeReported),
            handler.Request);
    }

    [Theory]
    [InlineData(512 * 1024, 512 * 1024)]
    [InlineData(64 * 1024 * 1024, 512 * 1024 * 1024)]
    public async Task OwnerRpc_AcceptsRuntimeProfilesOutsideFormerAgentCeilings(
        int valueBytes,
        int contentBytes)
    {
        using var rpcState = new RegressionTestRpcState();
        var expected = new AgentTransportProfile(
            valueBytes,
            valueBytes,
            valueBytes,
            contentBytes,
            RuntimeReported: true);
        var owner = CreateOwnerClient(
            rpcState,
            ResourceProfile(valueBytes, contentBytes),
            expected,
            out _);

        Assert.Equal(expected, await owner.GetTransportProfileAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("request")]
    [InlineData("response")]
    [InlineData("event")]
    [InlineData("content")]
    public async Task OwnerRpc_RejectsResponseAboveAnyProposedDimension(string dimension)
    {
        using var rpcState = new RegressionTestRpcState();
        var proposed = new AgentTransportProfile(
            8 * Mebibyte,
            8 * Mebibyte,
            8 * Mebibyte,
            64 * Mebibyte,
            RuntimeReported: true);
        var response = dimension switch
        {
            "request" => proposed with { RequestValueBytes = 16 * Mebibyte },
            "response" => proposed with { ResponseValueBytes = 16 * Mebibyte },
            "event" => proposed with { EventValueBytes = 16 * Mebibyte },
            "content" => proposed with { ContentReferenceBytes = 128 * Mebibyte },
            _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
        };
        var owner = CreateOwnerClient(
            rpcState,
            ResourceProfile(proposed.RequestValueBytes, proposed.ContentReferenceBytes),
            response,
            out _);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            owner.GetTransportProfileAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task OwnerRpc_RejectsReportedResponseWhenItsProposalWasUnreported()
    {
        using var rpcState = new RegressionTestRpcState();
        var owner = CreateOwnerClient(
            rpcState,
            resourceProfile: null,
            AgentRuntimePayloadLimits.StandardFallbackProfile with { RuntimeReported = true },
            out _);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            owner.GetTransportProfileAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public void AroundTurnPayload_RetainsRequestedActivityItemWhenAnchorRequiresProjection()
    {
        var turnId = Guid.NewGuid();
        var items = Enumerable.Range(0, 40)
            .Select(index => new AgentTurnItemRecord(
                Guid.NewGuid(),
                turnId,
                index,
                AgentTurnItemKind.ToolCall,
                TextContent: null,
                $"call-{index}",
                "large_tool",
                JsonSerializer.Serialize(new { value = new string('x', 150_000) }),
                ResultSummary: null,
                StructuredPayloadJson: null,
                SourcesJson: null,
                WasTruncated: false,
                IsError: false,
                ErrorCode: null,
                BackendId: null))
            .ToArray();
        var requestedItemId = items[^1].ItemId;
        var now = DateTimeOffset.UtcNow;
        var turn = new AgentTurnRecord(
            turnId,
            Guid.NewGuid(),
            AgentMessageRole.Assistant,
            AgentTurnKind.ToolCall,
            items,
            now,
            now);
        var page = new AgentTranscriptAroundTurnPage(1, [turn], true, true, turnId);

        var fitted = AgentRuntimePayloadLimits.FitAroundTurnPage(
            page,
            requestedItemId,
            ResponsePayloadBytes);

        Assert.True(AgentRuntimePayloadLimits.GetSerializedByteCount(fitted)
                    <= ResponsePayloadBytes);
        Assert.Contains(Assert.Single(fitted.Turns).Items, item => item.ItemId == requestedItemId);

        var subsession = SubsessionAroundTurnPayload.Fit(
            new SubsessionAroundTurnPage([AgentRoleBoundary.As<SubagentModels.AgentTurnRecord>(turn)], true, true, turnId),
            requestedItemId);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(subsession).Length
                    <= SubsessionAroundTurnPayload.MaximumResponseBytes);
        Assert.Contains(Assert.Single(subsession.Turns).Items, item => item.ItemId == requestedItemId);
    }

    [Fact]
    public async Task AttachmentGateway_TransfersEachAttachmentAsCallScopedContent()
    {
        var upload = CreateBytes((5 * 1024 * 1024) + 137);
        var download = CreateBytes((7 * 1024 * 1024) + 251);
        var client = new BoundedAttachmentOwnerClient(download);
        using var gateway = new AgentAppRuntimeGateway(client);
        var sessionId = Guid.NewGuid();

        _ = await gateway.QueueUserMessageAsync(
            sessionId,
            "profile",
            "Inspect the attachment.",
            "workspace",
            [new AgentAttachmentUploadRequest("large.bin", "application/octet-stream", upload)]);
        var read = await gateway.ReadAttachmentBytesAsync(new AgentAttachmentMetadata(
            Guid.NewGuid(),
            "download.bin",
            "application/octet-stream",
            AgentAttachmentKind.Binary,
            download.Length,
            string.Empty,
            "unused",
            IsText: false,
            WasTruncated: false));

        Assert.Equal(upload, client.UploadedContent);
        Assert.Equal(download, read);
        Assert.NotNull(client.RunCommand);
        Assert.Single(client.RunCommand!.AttachmentHandles!);
        Assert.True(client.MaximumRequestBytes < TestProfile.RequestValueBytes);
        Assert.True(client.MaximumResponseBytes < TestProfile.ResponseValueBytes);
        Assert.Equal(1, client.UploadContentCount);
        Assert.Equal(1, client.DownloadContentCount);
    }

    [Fact]
    public async Task OwnerRpc_TransfersAttachmentContentThroughCallScope()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        await using var services = RegressionTestRuntime.Create(scope.Context, catalog);
        var client = catalog.AddOwnerRuntimeProvider(
            services.GetRequiredService<WorkerRuntime.AgentOwnerRuntimeProvider>());
        var uploads = services.GetRequiredService<AgentAttachmentTransferService>();
        var attachments = services.GetRequiredService<AgentAttachmentService>();
        var content = CreateBytes((2 * 1024 * 1024) + 137);
        var request = new AgentAttachmentUploadRequest(
            "round-trip.bin",
            "application/octet-stream",
            content);

        var handle = await client.UploadAttachmentAsync(request, CancellationToken.None);
        var workerHandle = AgentRoleBoundary.As<WorkerRuntime.AgentAttachmentUploadHandle>(handle);
        var adoption = uploads.BeginAdoption([workerHandle]);
        try
        {
            Assert.Equal(content, await File.ReadAllBytesAsync(Assert.Single(adoption.Uploads).SourcePath));
        }
        finally
        {
            uploads.ReleaseAdoption(adoption);
        }

        var stored = await attachments.StoreAttachmentAsync(Guid.NewGuid(), AgentRoleBoundary.As<Sunder.Package.Agent.Contracts.Models.AgentAttachmentUploadRequest>(request));
        var downloaded = await client.DownloadAttachmentAsync(AgentRoleBoundary.As<AgentAttachmentMetadata>(stored.Metadata), CancellationToken.None);

        Assert.Equal(content, downloaded);
        await client.DiscardAttachmentAsync(handle, CancellationToken.None);
        Assert.Throws<AgentAttachmentTransferOperationException>(() => uploads.BeginAdoption([workerHandle]));
    }

    [Fact]
    public async Task AttachmentGateway_RejectsEscapedRunMessageAboveSerializedRequestLimit()
    {
        var client = new BoundedAttachmentOwnerClient([]);
        using var gateway = new AgentAppRuntimeGateway(client);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateway.QueueUserMessageAsync(
                Guid.NewGuid(),
                "profile",
                new string('\u0001', 180 * 1024),
                "workspace",
                []));

        Assert.Contains("negotiated Runtime value limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(client.RunCommand);
    }

    [Fact]
    public void TranscriptAndChangeProjections_FitCoreResponseAndEventLimits()
    {
        var turn = CreateTurn(new string('\\', 6 * 1024 * 1024));
        var page = AgentRuntimePayloadLimits.FitTranscriptPage(
            new AgentTranscriptPage(7, [turn], HasMore: false),
            AgentTranscriptPageDirection.Turn,
            maximumBytes: ResponsePayloadBytes);

        Assert.True(page.HasMore);
        Assert.Single(page.Turns);
        Assert.True(Assert.Single(page.Turns[0].Items).WasTruncated);
        Assert.Equal(turn.TurnId, page.Continuation?.TurnId);
        Assert.Contains(
            "Content truncated for display",
            page.Turns[0].Items[0].TextContent,
            StringComparison.Ordinal);
        Assert.True(
            AgentRuntimePayloadLimits.GetSerializedByteCount(page)
            <= ResponsePayloadBytes);

        var oversized = new AgentRuntimeChange(
            8,
            AgentRuntimeChangeKind.Turn,
            SessionId: turn.SessionId,
            Turn: turn,
            RuntimeInstanceId: "runtime");
        var projected = AgentRuntimePayloadLimits.ReplaceWithResnapshotIfOversized(
            oversized,
            EventPayloadBytes,
            oversized);

        Assert.Equal(AgentRuntimeChangeKind.ResnapshotRequired, projected.Kind);
        Assert.True(
            AgentRuntimePayloadLimits.GetSerializedByteCount(projected)
            <= EventPayloadBytes);

        var subsessionChange = SubsessionAroundTurnPayload.FitChange(new SubagentChanged(
            9,
            SubagentChangeKind.Turn,
            SessionId: turn.SessionId,
            Turn: AgentRoleBoundary.As<SubagentModels.AgentTurnRecord>(turn),
            RuntimeInstanceId: "subagent-runtime"));
        Assert.Equal(SubagentChangeKind.ResnapshotRequired, subsessionChange.Kind);
        Assert.True(
            SubsessionAroundTurnPayload.Size(subsessionChange)
            <= SubsessionAroundTurnPayload.MaximumEventBytes);
    }

    [Fact]
    public void ChangeProjection_PreservesTextAndUsesResnapshotForOversizedEvents()
    {
        var now = DateTimeOffset.UtcNow;
        var title = new string('t', 513);
        var activityText = new string('a', (16 * 1024) + 1);
        var change = new AgentRuntimeChange(
            8,
            AgentRuntimeChangeKind.RunActivity,
            SessionId: Guid.NewGuid(),
            Session: new AgentSessionSnapshot(
                new AgentSessionRecord(Guid.NewGuid(), title, AgentSessionState.Active, now, now),
                null),
            RunActivity: new AgentRunActivityUpdate(1, AgentRunActivityKind.Thinking, activityText, now),
            RuntimeInstanceId: "runtime");

        var projected = AgentRuntimePayloadLimits.ProjectChange(change);

        Assert.Equal(title, projected.Session!.Session.Title);
        Assert.Equal(activityText, projected.RunActivity!.Text);

        var oversized = projected with
        {
            RunActivity = projected.RunActivity with { Text = new string('x', EventPayloadBytes) },
        };
        var bounded = AgentRuntimePayloadLimits.ReplaceWithResnapshotIfOversized(
            oversized,
            EventPayloadBytes,
            oversized);

        Assert.Equal(AgentRuntimeChangeKind.ResnapshotRequired, bounded.Kind);
        Assert.Null(bounded.RunActivity);
        Assert.Null(bounded.Session);
    }

    [Fact]
    public void ToolDetailProjections_FitAgentAndSubsessionResponseLimits()
    {
        var sessionId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var payload = new string('\\', 2 * 1024 * 1024);
        var detail = new AgentTranscriptToolDetailRecord(
            sessionId,
            executionId,
            "call",
            itemId,
            Guid.NewGuid(),
            "large_tool",
            payload,
            payload,
            payload,
            payload,
            payload,
            payload,
            WasTruncated: false,
            IsError: false,
            ErrorCode: null,
            BackendId: "backend",
            AgentToolExecutionStatus.Completed,
            ToolOwnerPackageId: "package",
            ToolSchemaId: "schema",
            ToolSchemaVersion: "1",
            Revision: 42);

        var agent = AgentRuntimePayloadLimits.FitToolDetail(detail, ResponsePayloadBytes);
        var subsession = SubsessionAroundTurnPayload.FitToolDetail(AgentRoleBoundary.As<SubagentModels.AgentTranscriptToolDetailRecord>(detail));

        Assert.True(agent.WasTransportTruncated);
        Assert.Equal(sessionId, agent.SessionId);
        Assert.Equal(executionId, agent.ToolExecutionId);
        Assert.Equal(42, agent.Revision);
        Assert.True(AgentRuntimePayloadLimits.GetSerializedByteCount(
                        new AgentTranscriptToolDetailResult(Found: true, agent))
                    <= ResponsePayloadBytes);
        Assert.True(subsession.WasTransportTruncated);
        Assert.Equal(sessionId, subsession.SessionId);
        Assert.Equal(executionId, subsession.ToolExecutionId);
        Assert.Equal(42, subsession.Revision);
        Assert.True(SubsessionAroundTurnPayload.Size(new SubagentProjection(ToolDetail: subsession))
                    <= SubsessionAroundTurnPayload.MaximumResponseBytes);
    }

    [Fact]
    public void OversizedSingleTurnRemainsReachableAcrossInitialOlderAndNewerPages()
    {
        var turn = CreateTurn(new string('x', 6 * 1024 * 1024));
        foreach (var direction in new[]
                 {
                     AgentTranscriptPageDirection.Recent,
                     AgentTranscriptPageDirection.Before,
                     AgentTranscriptPageDirection.After,
                 })
        {
            var page = AgentRuntimePayloadLimits.FitTranscriptPage(
                new AgentTranscriptPage(13, [turn], HasMore: false),
                direction,
                maximumBytes: ResponsePayloadBytes);

            AssertProjectedTurn(page.Turns, page.HasMore, page.Continuation?.TurnId, turn.TurnId);
            Assert.True(AgentRuntimePayloadLimits.GetSerializedByteCount(page)
                        <= ResponsePayloadBytes);
            var roundTrip = Assert.IsType<AgentTranscriptPage>(JsonSerializer.Deserialize<AgentTranscriptPage>(
                JsonSerializer.SerializeToUtf8Bytes(page, JsonOptions),
                JsonOptions));
            Assert.Equal(turn.TurnId, roundTrip.Continuation?.TurnId);
        }

        foreach (var direction in new[]
                 {
                     SubagentQueryKind.RecentTurns,
                     SubagentQueryKind.TurnsBefore,
                     SubagentQueryKind.TurnsAfter,
                 })
        {
            var page = SubsessionLocalRuntimeAdapter.BuildTranscriptPage([AgentRoleBoundary.As<SubagentModels.AgentTurnRecord>(turn)], 30, direction);

            AssertProjectedTurn(page.Turns, page.HasMore, page.Continuation?.TurnId, turn.TurnId);
            Assert.True(SubsessionAroundTurnPayload.Size(page)
                        <= SubsessionAroundTurnPayload.MaximumResponseBytes);
            var roundTrip = Assert.IsType<SubsessionTranscriptPage>(
                JsonSerializer.Deserialize<SubsessionTranscriptPage>(
                    JsonSerializer.SerializeToUtf8Bytes(page, JsonOptions),
                    JsonOptions));
            Assert.Equal(turn.TurnId, roundTrip.Continuation?.TurnId);
        }
    }

    [Fact]
    public void OversizedDirectionalTranscriptPagesKeepCursorAdjacentTurnsAndContinuation()
    {
        var turns = Enumerable.Range(0, 31)
            .Select(index => CreateTurn($"{index:D2}:{new string('x', 180_000)}"))
            .ToArray();

        var agentBefore = AgentRuntimePayloadLimits.FitTranscriptPage(
            new AgentTranscriptPage(11, turns[1..], HasMore: true),
            AgentTranscriptPageDirection.Before,
            maximumBytes: ResponsePayloadBytes);
        var agentRecent = AgentRuntimePayloadLimits.FitTranscriptPage(
            new AgentTranscriptPage(11, turns[1..], HasMore: true),
            AgentTranscriptPageDirection.Recent,
            maximumBytes: ResponsePayloadBytes);
        var agentAfter = AgentRuntimePayloadLimits.FitTranscriptPage(
            new AgentTranscriptPage(11, turns[..30], HasMore: true),
            AgentTranscriptPageDirection.After,
            maximumBytes: ResponsePayloadBytes);
        AssertDirectionalPage(agentBefore.Turns, agentBefore.HasMore, turns[^1].TurnId);
        AssertDirectionalPage(agentRecent.Turns, agentRecent.HasMore, turns[^1].TurnId);
        AssertDirectionalPage(agentAfter.Turns, agentAfter.HasMore, turns[0].TurnId);
        Assert.True(AgentRuntimePayloadLimits.GetSerializedByteCount(agentBefore)
                    <= ResponsePayloadBytes);
        Assert.True(AgentRuntimePayloadLimits.GetSerializedByteCount(agentRecent)
                    <= ResponsePayloadBytes);
        Assert.True(AgentRuntimePayloadLimits.GetSerializedByteCount(agentAfter)
                    <= ResponsePayloadBytes);
        Assert.Equal(agentBefore.Turns[0].TurnId, agentBefore.Continuation?.TurnId);
        Assert.Equal(agentRecent.Turns[0].TurnId, agentRecent.Continuation?.TurnId);
        Assert.Equal(agentAfter.Turns[^1].TurnId, agentAfter.Continuation?.TurnId);

        var subsessionRecent = SubsessionLocalRuntimeAdapter.BuildTranscriptPage(
            AgentRoleBoundary.As<IReadOnlyList<SubagentModels.AgentTurnRecord>>(turns),
            30,
            SubagentQueryKind.RecentTurns);
        var subsessionBefore = SubsessionLocalRuntimeAdapter.BuildTranscriptPage(
            AgentRoleBoundary.As<IReadOnlyList<SubagentModels.AgentTurnRecord>>(turns),
            30,
            SubagentQueryKind.TurnsBefore);
        var subsessionAfter = SubsessionLocalRuntimeAdapter.BuildTranscriptPage(
            AgentRoleBoundary.As<IReadOnlyList<SubagentModels.AgentTurnRecord>>(turns),
            30,
            SubagentQueryKind.TurnsAfter);
        AssertDirectionalPage(
            subsessionRecent.Turns,
            subsessionRecent.HasMore,
            turns[^1].TurnId);
        AssertDirectionalPage(
            subsessionBefore.Turns,
            subsessionBefore.HasMore,
            turns[^1].TurnId);
        AssertDirectionalPage(
            subsessionAfter.Turns,
            subsessionAfter.HasMore,
            turns[0].TurnId);
        Assert.True(SubsessionAroundTurnPayload.Size(subsessionBefore)
                    <= SubsessionAroundTurnPayload.MaximumResponseBytes);
        Assert.True(SubsessionAroundTurnPayload.Size(subsessionRecent)
                    <= SubsessionAroundTurnPayload.MaximumResponseBytes);
        Assert.True(SubsessionAroundTurnPayload.Size(subsessionAfter)
                    <= SubsessionAroundTurnPayload.MaximumResponseBytes);
        Assert.Equal(subsessionBefore.Turns[0].TurnId, subsessionBefore.Continuation?.TurnId);
        Assert.Equal(subsessionRecent.Turns[0].TurnId, subsessionRecent.Continuation?.TurnId);
        Assert.Equal(subsessionAfter.Turns[^1].TurnId, subsessionAfter.Continuation?.TurnId);
    }

    private static void AssertProjectedTurn(
        object roleTurns,
        bool hasMore,
        Guid? continuationTurnId,
        Guid expectedTurnId)
    {
        var turns = AgentRoleBoundary.As<IReadOnlyList<AgentTurnRecord>>(roleTurns);
        Assert.True(hasMore);
        var turn = Assert.Single(turns);
        Assert.Equal(expectedTurnId, turn.TurnId);
        Assert.Equal(expectedTurnId, continuationTurnId);
        var item = Assert.Single(turn.Items);
        Assert.True(item.WasTruncated);
        Assert.Contains("Content truncated for display", item.TextContent, StringComparison.Ordinal);
    }

    private static void AssertDirectionalPage(
        object roleTurns,
        bool hasMore,
        Guid cursorAdjacentTurnId)
    {
        var turns = AgentRoleBoundary.As<IReadOnlyList<AgentTurnRecord>>(roleTurns);
        Assert.True(hasMore);
        Assert.InRange(turns.Count, 1, 29);
        Assert.Contains(turns, turn => turn.TurnId == cursorAdjacentTurnId);
    }

    private static AgentTurnRecord CreateTurn(string content)
    {
        var turnId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return new AgentTurnRecord(
            turnId,
            Guid.NewGuid(),
            AgentMessageRole.Assistant,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                turnId,
                0,
                AgentTurnItemKind.Text,
                content,
                null,
                null,
                null,
                null,
                null,
                null,
                WasTruncated: false,
                IsError: false,
                ErrorCode: null,
                BackendId: null)],
            now,
            now);
    }

    private static byte[] CreateBytes(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }
        return bytes;
    }

    private static SunderRpcResourceProfile ResourceProfile(int valueBytes, int contentBytes)
        => new(
            new string('a', 64),
            valueBytes,
            MaximumValueDepth: 64,
            MaximumValueNodes: 524_288,
            MaximumObjectMembers: 4_096,
            MaximumInlineDecodedBytes: 512 * 1024,
            contentBytes);

    private static AgentOwnerRpcClient CreateOwnerClient(
        RegressionTestRpcState state,
        SunderRpcResourceProfile? resourceProfile,
        AgentTransportProfile response,
        out TransportProfileRpcHandler handler)
    {
        handler = new TransportProfileRpcHandler(response);
        state.Add(
            "sunder.package.agent",
            RegressionTestRpcDescriptors.Get("sunder.agent.owner.runtime"),
            handler,
            handler,
            handler,
            "agent.owner.runtime", packageVersion: "2.0.0");
        return new AgentOwnerRpcClient(new RegressionTestRpcClient(
            state,
            "sunder.package.agent",
            resourceProfile));
    }

    private sealed class TransportProfileRpcHandler(AgentTransportProfile response)
        : ISunderRpcServiceHandler, IDisposable
    {
        public AgentTransportProfileRequest? Request { get; private set; }

        public ValueTask<JsonElement> InvokeUnaryAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request.Deserialize<AgentTransportProfileRequest>(JsonOptions)
                      ?? throw new InvalidDataException("Transport proposal was not deserializable.");
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(response, JsonOptions));
        }

        public async IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public void Dispose()
        {
        }
    }

    private sealed class BoundedAttachmentOwnerClient(byte[] downloadContent)
        : IAgentOwnerClient
    {
        private readonly Dictionary<string, byte[]> _uploads = new(StringComparer.Ordinal);
        private readonly byte[] _downloadContent = downloadContent;

        public AgentTransportProfile TransportProfile => TestProfile;
        public int MaximumRequestBytes { get; private set; }
        public int MaximumResponseBytes { get; private set; }
        public int UploadContentCount { get; private set; }
        public int DownloadContentCount { get; private set; }
        public byte[]? UploadedContent { get; private set; }
        public AgentRunCommand? RunCommand { get; private set; }

        public ValueTask<AgentTransportProfile> GetTransportProfileAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(TransportProfile);
        }

        public ValueTask<AgentAttachmentUploadHandle> UploadAttachmentAsync(
            AgentAttachmentUploadRequest upload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.InRange(upload.Content.Length, 1, TransportProfile.ContentReferenceBytes);
            var transferId = Guid.NewGuid().ToString("N");
            _uploads.Add(transferId, upload.Content.ToArray());
            UploadContentCount++;
            return ValueTask.FromResult(new AgentAttachmentUploadHandle(transferId));
        }

        public ValueTask<byte[]> DownloadAttachmentAsync(
            AgentAttachmentMetadata metadata,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_downloadContent.Length, metadata.SizeBytes);
            DownloadContentCount++;
            return ValueTask.FromResult(_downloadContent.ToArray());
        }

        public ValueTask DiscardAttachmentAsync(
            AgentAttachmentUploadHandle handle,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _uploads.Remove(handle.TransferId);
            return ValueTask.CompletedTask;
        }

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            AgentOwnerRpcOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions).Length;
            MaximumRequestBytes = Math.Max(MaximumRequestBytes, requestBytes);
            if (requestBytes > AgentRuntimePayloadLimits.GetRequestPayloadBytes(TransportProfile))
            {
                throw new InvalidDataException("Request exceeded the simulated Core transport limit.");
            }

            object response;
            if (operation.Is(AgentRuntimeOperations.Runs))
            {
                RunCommand = (AgentRunCommand)(object)request;
                var handle = Assert.Single(RunCommand.AttachmentHandles!);
                UploadedContent = _uploads[handle.TransferId];
                _uploads.Remove(handle.TransferId);
                response = new AgentRunCommandResult(
                    1,
                    new AgentRunCheckpointRecord(
                        Guid.NewGuid(),
                        RunCommand.SessionId,
                        1,
                        AgentRunStatus.Running,
                        "Running.",
                        DateTimeOffset.UtcNow));
            }
            else
            {
                throw new NotSupportedException(operation.Describe());
            }

            var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions).Length;
            MaximumResponseBytes = Math.Max(MaximumResponseBytes, responseBytes);
            if (responseBytes > AgentRuntimePayloadLimits.GetResponsePayloadBytes(TransportProfile))
            {
                throw new InvalidDataException("Response exceeded the simulated Core transport limit.");
            }
            return ValueTask.FromResult((TResponse)response);
        }

        public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            AgentOwnerRpcStream<TRequest, TEvent> stream,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
        {
            await Task.CompletedTask;
            yield break;
        }

    }
}
