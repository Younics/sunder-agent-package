extern alias AgentCore;

using AgentCore::Sunder.Package.Agent.Contracts.Models;
using Xunit;
using CorePresentation = AgentCore::Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Tests;

public sealed class TranscriptToolDetailStateTests
{
    [Fact]
    public void ThousandCollapsedTools_ProjectHeadersWithoutLoadingOrResolvingDetails()
    {
        const int count = 1000;
        var sessionId = Guid.NewGuid();
        var heavyPayload = new string('x', 32 * 1024);
        var loads = 0;
        var resolutions = 0;
        var states = new List<CorePresentation.TranscriptToolDetailState>(count);

        for (var index = 0; index < count; index++)
        {
            var turnId = Guid.NewGuid();
            var item = new AgentTurnItemRecord(
                Guid.NewGuid(),
                turnId,
                0,
                AgentTurnItemKind.ToolResult,
                heavyPayload,
                $"call-{index}",
                "large_tool",
                heavyPayload,
                heavyPayload,
                heavyPayload,
                heavyPayload,
                WasTruncated: false,
                IsError: false,
                ErrorCode: heavyPayload,
                BackendId: heavyPayload,
                PresentationPayloadJson: heavyPayload)
            {
                ToolExecutionId = Guid.NewGuid(),
                ToolExecutionStatus = AgentToolExecutionStatus.Completed,
                ToolOwnerPackageId = heavyPayload,
                ToolSchemaId = heavyPayload,
                ToolSchemaVersion = heavyPayload,
            };
            var turn = new AgentTurnRecord(
                turnId,
                sessionId,
                AgentMessageRole.Tool,
                AgentTurnKind.ToolResult,
                [item],
                DateTimeOffset.UnixEpoch.AddSeconds(index),
                DateTimeOffset.UnixEpoch.AddSeconds(index + 1));
            var projectedTurn = CorePresentation.TranscriptTurnTransportProjection.ProjectToolHeaders(turn);
            var projectedItem = Assert.Single(projectedTurn.Items);

            Assert.True(projectedItem.IsToolHeaderProjection);
            Assert.Null(projectedItem.TextContent);
            Assert.Null(projectedItem.ArgumentsJson);
            Assert.Null(projectedItem.ResultSummary);
            Assert.Null(projectedItem.StructuredPayloadJson);
            Assert.Null(projectedItem.SourcesJson);
            Assert.Null(projectedItem.PresentationPayloadJson);
            Assert.Null(projectedItem.ErrorCode);
            Assert.Null(projectedItem.BackendId);
            Assert.Null(projectedItem.ToolOwnerPackageId);
            Assert.Null(projectedItem.ToolSchemaId);
            Assert.Null(projectedItem.ToolSchemaVersion);
            Assert.True(projectedItem.ToolHasDetails);

            var projection = CorePresentation.TranscriptRowProjector<object>.DescribeTool(
                projectedTurn,
                projectedItem);
            states.Add(new CorePresentation.TranscriptToolDetailState(
                projection,
                (_, _) =>
                {
                    loads++;
                    return Task.FromResult<AgentTranscriptToolDetailRecord?>(null);
                },
                (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    resolutions++;
                    return ValueTask.FromResult(new AgentToolPresentation());
                }));
        }

        Assert.Equal(0, loads);
        Assert.Equal(0, resolutions);
        Assert.All(states, state =>
        {
            Assert.Equal(CorePresentation.TranscriptToolExpansionState.Collapsed, state.State);
            Assert.Null(state.Details);
        });

        foreach (var state in states)
        {
            state.Dispose();
        }
    }

    [Theory]
    [InlineData(17)]
    [InlineData(18)]
    public async Task Expansion_AcceptsExactOrNewerAuthoritativeDetailsWithoutRedundantRefresh(long returnedRevision)
    {
        var projection = CreateProjection(revision: 17);
        var loads = 0;
        var resolutions = 0;
        using var state = new CorePresentation.TranscriptToolDetailState(
            projection,
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                loads++;
                return Task.FromResult<AgentTranscriptToolDetailRecord?>(CreateDetail(request, revision: returnedRevision));
            },
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                resolutions++;
                return ValueTask.FromResult(new AgentToolPresentation("header", "detail", "output"));
            });
        var request = Assert.IsType<CorePresentation.TranscriptToolExpansionRequest>(state.BeginExpansion());

        var detail = await state.LoadAsync(request, CancellationToken.None);

        Assert.True(state.TryMaterialize(request, detail, out var materialized));
        Assert.NotNull(materialized);
        Assert.True(state.CommitExpanded(request));
        Assert.Equal(1, loads);
        Assert.Equal(1, resolutions);
        Assert.Equal(CorePresentation.TranscriptToolExpansionState.Expanded, state.State);
        Assert.Same(materialized, state.Details);
        var refreshes = 0;
        state.RefreshRequested += () => refreshes++;
        state.UpdateProjection(projection with { DetailRevision = returnedRevision });
        Assert.Equal(0, refreshes);
        Assert.True(state.IsExpanded);

        state.Collapse();
        Assert.Equal(CorePresentation.TranscriptToolExpansionState.Collapsed, state.State);
        Assert.Null(state.Details);
    }

    [Fact]
    public async Task RevisionChange_CancelsInFlightLoadAndRejectsStaleDetail()
    {
        var projection = CreateProjection(revision: 21);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolutions = 0;
        using var state = new CorePresentation.TranscriptToolDetailState(
            projection,
            async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            },
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                resolutions++;
                return ValueTask.FromResult(new AgentToolPresentation());
            });
        var request = Assert.IsType<CorePresentation.TranscriptToolExpansionRequest>(state.BeginExpansion());
        var load = state.LoadAsync(request, CancellationToken.None);
        await entered.Task;

        state.UpdateProjection(projection with { DetailRevision = 22 });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.False(state.IsCurrent(request));
        Assert.False(state.TryMaterialize(request,
            new CorePresentation.TranscriptToolDetailData(CreateDetail(request.DetailRequest, revision: 21), new AgentToolPresentation()), out _));
        Assert.Equal(0, resolutions);
        Assert.Equal(CorePresentation.TranscriptToolExpansionState.Collapsed, state.State);
        Assert.Null(state.Details);
    }

    [Fact]
    public async Task ExpandedRevisionChange_KeepsVisibleDetailsUntilExactReplacementCommitsAndCollapseRejectsLateRefresh()
    {
        var projection = CreateProjection(revision: 31);
        var currentRevision = 31L;
        using var state = new CorePresentation.TranscriptToolDetailState(
            projection,
            (request, _) => Task.FromResult<AgentTranscriptToolDetailRecord?>(
                CreateDetail(request, revision: currentRevision)),
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new AgentToolPresentation("header", "loaded detail", "output"));
            });
        var request = Assert.IsType<CorePresentation.TranscriptToolExpansionRequest>(state.BeginExpansion());
        var detail = await state.LoadAsync(request, CancellationToken.None);
        Assert.True(state.TryMaterialize(request, detail, out var materialized));
        Assert.True(state.CommitExpanded(request));
        var invalidations = 0;
        state.Invalidated += () =>
        {
            invalidations++;
            Assert.True(state.Details!.DetailMarkdownBuilder.Length > 0);
        };

        var refreshes = 0;
        state.RefreshRequested += () => refreshes++;
        currentRevision = 32;
        state.UpdateProjection(projection with { DetailRevision = 32 });
        Assert.Equal(1, refreshes);
        Assert.Equal(0, invalidations);
        Assert.True(state.IsExpanded);
        Assert.Same(materialized, state.Details);
        var refresh = Assert.IsType<CorePresentation.TranscriptToolExpansionRequest>(state.BeginExpansion());
        Assert.True(state.IsExpanded);
        state.UpdateProjection(projection); // A delayed call header cannot supersede its newer result's refresh.
        Assert.True(state.IsCurrent(refresh));
        var replacement = await state.LoadAsync(refresh, CancellationToken.None);
        Assert.True(state.TryMaterialize(refresh, replacement, out var prepared));
        Assert.Same(materialized, state.Details);
        Assert.True(materialized!.DetailMarkdownBuilder.Length > 0);
        Assert.True(state.CommitExpanded(refresh));
        Assert.Same(prepared, state.Details);
        Assert.Equal(0, materialized.DetailMarkdownBuilder.Length);

        currentRevision = 33;
        state.UpdateProjection(projection with { DetailRevision = 33 });
        var pending = Assert.IsType<CorePresentation.TranscriptToolExpansionRequest>(state.BeginExpansion());
        var late = await state.LoadAsync(pending, CancellationToken.None);
        state.Collapse();
        Assert.False(state.TryMaterialize(pending, late, out _));
        Assert.False(state.CommitExpanded(pending));
        Assert.Equal(1, invalidations);
        Assert.Null(state.Details);
    }

    [Fact]
    public async Task FailedRefreshKeepsPreviousDetailsButChangedIdentityAndCollapseRemainAuthoritative()
    {
        var projection = CreateProjection(revision: 40);
        var revision = 40L;
        using var state = new CorePresentation.TranscriptToolDetailState(projection,
            (request, _) => Task.FromResult<AgentTranscriptToolDetailRecord?>(CreateDetail(request, revision)),
            (_, _) => ValueTask.FromResult(new AgentToolPresentation("header", "recorded detail", "output")));
        var initial = Assert.IsType<CorePresentation.TranscriptToolExpansionRequest>(state.BeginExpansion());
        Assert.True(state.TryMaterialize(initial, await state.LoadAsync(initial, CancellationToken.None), out var visible));
        Assert.True(state.CommitExpanded(initial));
        state.UpdateProjection(projection with { DetailRevision = ++revision });
        var refresh = Assert.IsType<CorePresentation.TranscriptToolExpansionRequest>(state.BeginExpansion());
        state.Fail(refresh, new IOException("read unavailable"));
        Assert.True(state.IsExpanded);
        Assert.Same(visible, state.Details);
        Assert.True(visible!.DetailMarkdownBuilder.Length > 0);
        Assert.Contains("Showing the previous details", state.FailureText, StringComparison.Ordinal);
        var retry = Assert.IsType<CorePresentation.TranscriptToolExpansionRequest>(state.BeginExpansion());
        var replacement = await state.LoadAsync(retry, CancellationToken.None);
        Assert.True(state.TryMaterialize(retry, replacement, out var staged));
        state.UpdateProjection(projection with { CallId = "different-call", DetailRevision = revision });
        Assert.False(state.IsExpanded);
        Assert.Null(state.Details);
        Assert.Equal(0, visible.DetailMarkdownBuilder.Length);
        Assert.Equal(0, staged!.DetailMarkdownBuilder.Length);
        Assert.False(state.CommitExpanded(retry));
        Assert.False(state.TryMaterialize(retry, replacement, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleDetail_RereadsOnceWithoutResolvingTheWrongRevision(bool catchesUp)
    {
        var reads = 0;
        var resolutions = 0;
        using var state = new CorePresentation.TranscriptToolDetailState(CreateProjection(51),
            (request, _) => Task.FromResult<AgentTranscriptToolDetailRecord?>(
                CreateDetail(request, ++reads == 2 && catchesUp ? 51 : 50)),
            (_, _) =>
            {
                resolutions++;
                return ValueTask.FromResult(new AgentToolPresentation());
            });
        var request = state.BeginExpansion()!;
        if (catchesUp)
        {
            var loaded = await state.LoadAsync(request, default);
            Assert.True(state.TryMaterialize(request, loaded, out _));
            Assert.True(state.CommitExpanded(request));
            Assert.Equal(1, resolutions);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => state.LoadAsync(request, default));
            state.Fail(request, failure);
            Assert.Contains("changed while loading", state.FailureText, StringComparison.Ordinal);
            Assert.DoesNotContain("no longer available", state.FailureText, StringComparison.Ordinal);
            Assert.Equal(0, resolutions);
        }
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrForeignDetailsAreNotRetriedOrResolved(bool foreign)
    {
        var reads = 0;
        using var state = new CorePresentation.TranscriptToolDetailState(CreateProjection(61),
            (request, _) =>
            {
                reads++;
                return Task.FromResult<AgentTranscriptToolDetailRecord?>(foreign
                    ? CreateDetail(request, 60) with { SessionId = Guid.NewGuid() } : null);
            }, (_, _) => throw new InvalidOperationException("Must not resolve foreign/missing details."));
        Assert.Null(await state.LoadAsync(state.BeginExpansion()!, default));
        Assert.Equal(1, reads);
    }

    private static CorePresentation.TranscriptToolProjection CreateProjection(long revision)
    {
        var turnId = Guid.NewGuid();
        return new CorePresentation.TranscriptToolProjection(
            CorePresentation.TranscriptProjectedRowKind.ToolResult,
            Guid.NewGuid(),
            turnId,
            Guid.NewGuid(),
            null,
            null,
            Guid.NewGuid(),
            "call",
            "tool",
            "Tool",
            "Completed",
            "✓",
            "summary",
            string.Empty,
            revision,
            true,
            new CorePresentation.TranscriptRowAnchorKey("tool:call"));
    }

    private static AgentTranscriptToolDetailRecord CreateDetail(
        AgentTranscriptToolDetailRequest request,
        long revision)
        => new AgentTranscriptToolDetailRecord(
            request.SessionId,
            request.ToolExecutionId,
            request.CallId,
            request.ItemId,
            Guid.NewGuid(),
            "tool",
            "{\"path\":\"file.txt\"}",
            "output",
            "summary",
            null,
            null,
            null,
            WasTruncated: false,
            IsError: false,
            ErrorCode: null,
            BackendId: null,
            AgentToolExecutionStatus.Completed,
            ToolOwnerPackageId: null,
            ToolSchemaId: null,
            ToolSchemaVersion: null,
            revision)
        {
            RunId = request.RunId,
            RunRevision = request.RunRevision,
        };
}
