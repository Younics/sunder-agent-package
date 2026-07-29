extern alias AgentCore;

using Sunder.Package.Agent.Contracts.Models;
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
                _ =>
                {
                    resolutions++;
                    return new AgentToolPresentation();
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

    [Fact]
    public async Task Expansion_LoadsAndResolvesOneExactRevision()
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
                return Task.FromResult<AgentTranscriptToolDetailRecord?>(CreateDetail(request, revision: 17));
            },
            _ =>
            {
                resolutions++;
                return new AgentToolPresentation("header", "detail", "output");
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
            _ =>
            {
                resolutions++;
                return new AgentToolPresentation();
            });
        var request = Assert.IsType<CorePresentation.TranscriptToolExpansionRequest>(state.BeginExpansion());
        var load = state.LoadAsync(request, CancellationToken.None);
        await entered.Task;

        state.UpdateProjection(projection with { DetailRevision = 22 });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.False(state.IsCurrent(request));
        Assert.False(state.TryMaterialize(request, CreateDetail(request.DetailRequest, revision: 21), out _));
        Assert.Equal(0, resolutions);
        Assert.Equal(CorePresentation.TranscriptToolExpansionState.Collapsed, state.State);
        Assert.Null(state.Details);
    }

    [Fact]
    public async Task ExpandedRevisionChange_InvalidatesAttachedVisualBeforeDisposingDetails()
    {
        var projection = CreateProjection(revision: 31);
        using var state = new CorePresentation.TranscriptToolDetailState(
            projection,
            (request, _) => Task.FromResult<AgentTranscriptToolDetailRecord?>(
                CreateDetail(request, revision: 31)),
            _ => new AgentToolPresentation("header", "loaded detail", "output"));
        var request = Assert.IsType<CorePresentation.TranscriptToolExpansionRequest>(state.BeginExpansion());
        var detail = await state.LoadAsync(request, CancellationToken.None);
        Assert.True(state.TryMaterialize(request, detail, out var materialized));
        Assert.True(state.CommitExpanded(request));
        var invalidations = 0;
        var detailsWereAliveAtInvalidation = false;
        state.Invalidated += () =>
        {
            invalidations++;
            detailsWereAliveAtInvalidation = ReferenceEquals(materialized, state.Details)
                                             && materialized!.DetailMarkdownBuilder.Length > 0;
        };

        state.UpdateProjection(projection with { DetailRevision = 32 });

        Assert.Equal(1, invalidations);
        Assert.True(detailsWereAliveAtInvalidation);
        Assert.Equal(CorePresentation.TranscriptToolExpansionState.Collapsed, state.State);
        Assert.Null(state.Details);
        Assert.Equal(0, materialized!.DetailMarkdownBuilder.Length);
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
