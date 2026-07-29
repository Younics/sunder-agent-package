using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    internal async Task<AgentHistoryNavigationPlacement> NavigateToHistoryAnchorAsync(
        HistorySearchNavigationTarget target,
        CancellationToken cancellationToken)
    {
        if (_transcriptAnchorGateway is null)
        {
            throw new InvalidOperationException("Anchored transcript navigation requires Agent Runtime.");
        }

        var snapshotOperation = BeginChatSnapshotRequest();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            snapshotOperation.CancellationToken);
        var operationCancellation = linked.Token;
        var navigationTarget = ToTranscriptNavigationTarget(target);
        AgentHistoryNavigationPlacement? fastPlacement = null;
        await InvokeOnUiThreadAsync(() =>
        {
            EnsureCurrentChatSnapshotRequest(snapshotOperation.Generation, operationCancellation);
            ClearNavigationHighlightForPreparation();
            if (TryResolveLoadedHistoryAnchor(target, out var loadedAnchorKey)
                && _timeline.TrySelectLoadedAnchor(target.SessionId, loadedAnchorKey))
            {
                fastPlacement = new AgentHistoryNavigationPlacement(
                    loadedAnchorKey,
                    snapshotOperation.Generation,
                    UsesLoadedWindow: true,
                    Selection: null);
            }
        }).ConfigureAwait(false);
        if (fastPlacement is not null)
        {
            return fastPlacement;
        }

        AgentChatSnapshotProjection? snapshot = null;
        var snapshotApplied = false;
        try
        {
            snapshot = _chatSnapshotGateway is not null
                ? await _chatSnapshotGateway.LoadChatSnapshotAsync(
                    new AgentChatSnapshotRequest(
                        InitialTranscriptTurnLimit,
                        PreferredWorkspaceId: target.WorkspaceId,
                        PreferredSessionId: target.SessionId,
                        IncludeInitialTranscript: false),
                    operationCancellation).ConfigureAwait(false)
                : await LoadInProcessChatSnapshotAsync(
                    new AgentChatSnapshotRequest(
                        InitialTranscriptTurnLimit,
                        PreferredWorkspaceId: target.WorkspaceId,
                        PreferredSessionId: target.SessionId,
                        IncludeInitialTranscript: false),
                    operationCancellation).ConfigureAwait(false);
            EnsureCurrentChatSnapshotRequest(snapshotOperation.Generation, operationCancellation);
            EnsureSnapshotContainsTarget(snapshot, target);

            var page = await _transcriptAnchorGateway.LoadTranscriptAroundTurnAsync(
                new AgentTranscriptAroundTurnRequest(
                    target.SessionId,
                    target.TurnId,
                    ItemId: target.ItemId),
                operationCancellation).ConfigureAwait(false);
            EnsureCurrentChatSnapshotRequest(snapshotOperation.Generation, operationCancellation);
            if (page.AnchorTurnId != target.TurnId
                || !TryResolveAnchorKey(navigationTarget, page.Turns, out var anchorKey))
            {
                throw new AgentHistoryTargetUnavailableException();
            }

            await InvokeOnUiThreadAsync(() =>
            {
                EnsureCurrentChatSnapshotRequest(snapshotOperation.Generation, operationCancellation);
                ApplyChatSnapshotCore(
                    snapshot,
                    forceTranscriptReplacement: false,
                    applyTranscript: false);
                EnsureCurrentHistoryNavigation(
                    target,
                    snapshotOperation.Generation,
                    operationCancellation,
                    targetUnavailableIsError: true,
                    requireTimelineSelection: false);
                var ticket = _timeline.BeginAnchoredInitialLoad(target.SessionId);
                if (!_timeline.TryCompleteAnchoredInitialLoad(
                        ticket,
                        page.Turns,
                        page.HasOlder,
                        page.HasNewer,
                        anchorKey))
                {
                    throw new OperationCanceledException(
                        "The transcript navigation was superseded.",
                        operationCancellation);
                }
                ApplyRunActivityState();
                snapshotApplied = true;
            }).ConfigureAwait(false);
            return new AgentHistoryNavigationPlacement(
                anchorKey,
                snapshotOperation.Generation,
                UsesLoadedWindow: false,
                Selection: new AgentHistorySelection(
                    snapshot.SelectedProfile?.ProfileId,
                    snapshot.SelectedWorkspace?.WorkspaceId,
                    snapshot.SelectedSession?.Session.SessionId));
        }
        finally
        {
            if (snapshot is not null)
            {
                _chatSnapshotGateway?.CompleteChatSnapshot(snapshot, snapshotApplied);
            }
        }
    }

    private static TranscriptNavigationTarget ToTranscriptNavigationTarget(
        HistorySearchNavigationTarget target)
        => new(
            target.WorkspaceId,
            target.SessionId,
            target.TurnId,
            target.ItemId,
            target.CreatedAtUtc,
            target.CallId,
            target.AnchorKind == HistoryAnchorKind.Text
                ? TranscriptNavigationAnchorKind.Text
                : TranscriptNavigationAnchorKind.Activity);

    internal bool TryResolveLoadedHistoryAnchor(
        HistorySearchNavigationTarget target,
        out object anchorKey)
    {
        anchorKey = default!;
        return DisplayedTranscriptSessionId == target.SessionId
               && string.Equals(
                   SelectedWorkspace?.WorkspaceId,
                   target.WorkspaceId,
                   StringComparison.OrdinalIgnoreCase)
               && TryResolveAnchorKey(
                   ToTranscriptNavigationTarget(target),
                   _timeline.Projector.TurnWindow.OrderedTurns(),
                   out anchorKey);
    }

    private static bool TryResolveAnchorKey(
        TranscriptNavigationTarget target,
        IReadOnlyList<AgentTurnRecord> turns,
        out object anchorKey)
    {
        anchorKey = default!;
        var turn = turns.FirstOrDefault(candidate => candidate.TurnId == target.TurnId);
        if (turn is null)
        {
            return false;
        }
        if (target.AnchorKind == TranscriptNavigationAnchorKind.Text
            && !turn.Items.Any(item => item.ItemId == target.ItemId
                                       && item.Kind == AgentTurnItemKind.Text))
        {
            return false;
        }

        try
        {
            anchorKey = TranscriptAnchorNavigation.ResolveAnchorKey(target, turns);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void EnsureSnapshotContainsTarget(
        AgentChatSnapshotProjection snapshot,
        HistorySearchNavigationTarget target)
    {
        if (!string.Equals(
                snapshot.SelectedWorkspace?.WorkspaceId,
                target.WorkspaceId,
                StringComparison.OrdinalIgnoreCase)
            || snapshot.SelectedSession?.Session.SessionId != target.SessionId
            || snapshot.SelectedSession.Session.ParentSessionId is not null)
        {
            throw new AgentHistoryTargetUnavailableException();
        }
    }

    private void EnsureCurrentChatSnapshotRequest(
        int snapshotGeneration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentChatSnapshotRequest(snapshotGeneration))
        {
            throw new OperationCanceledException("The chat snapshot superseded transcript navigation.");
        }
    }

    private void EnsureCurrentHistoryNavigation(
        HistorySearchNavigationTarget target,
        int snapshotGeneration,
        CancellationToken cancellationToken,
        bool targetUnavailableIsError = false,
        bool requireTimelineSelection = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentChatSnapshotRequest(snapshotGeneration))
        {
            throw new OperationCanceledException("The chat snapshot superseded transcript navigation.");
        }

        var targetIsCurrent = string.Equals(
                SelectedWorkspace?.WorkspaceId,
                target.WorkspaceId,
                StringComparison.OrdinalIgnoreCase)
            && SelectedSession?.SessionId == target.SessionId
            && DisplayedTranscriptSessionId == target.SessionId
            && (!requireTimelineSelection || _timeline.SessionId == target.SessionId);
        if (targetIsCurrent)
        {
            return;
        }
        if (targetUnavailableIsError)
        {
            throw new InvalidOperationException("The selected root session is no longer available.");
        }

        throw new OperationCanceledException("The chat selection superseded transcript navigation.");
    }

    internal void EnsureCurrentHistoryPlacement(
        HistorySearchNavigationTarget target,
        AgentHistoryNavigationPlacement placement,
        CancellationToken cancellationToken)
        => EnsureCurrentHistoryNavigation(
            target,
            placement.SnapshotGeneration,
            cancellationToken);

    private void ClearNavigationHighlightForPreparation()
    {
        Interlocked.Increment(ref _navigationHighlightGeneration);
        _navigationHighlightCancellation?.Cancel();
        _navigationHighlightCancellation?.Dispose();
        _navigationHighlightCancellation = null;
        foreach (var row in Messages)
        {
            row.ClearNavigationHighlight();
        }
    }

    internal async Task AcknowledgeHistoryPresentationAsync(
        AgentHistoryNavigationPlacement placement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var acknowledged = false;
        await InvokeOnUiThreadAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentChatSnapshotRequest(placement.SnapshotGeneration)
                || _timeline.SelectedAnchorKey is not { } selectedAnchor
                || !Equals(selectedAnchor, placement.AnchorKey))
            {
                return;
            }

            _navigationHighlightCancellation?.Cancel();
            _navigationHighlightCancellation?.Dispose();
            _navigationHighlightCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            var highlightGeneration = Interlocked.Increment(ref _navigationHighlightGeneration);
            var highlightCancellation = _navigationHighlightCancellation.Token;
            foreach (var row in Messages)
            {
                if (Equals(row.AnchorKey, placement.AnchorKey))
                {
                    row.PrimeNavigationHighlight();
                }
                else
                {
                    row.ClearNavigationHighlight();
                }
            }
            StartNavigationHighlightLifetime(
                placement.AnchorKey,
                highlightGeneration,
                highlightCancellation);
            acknowledged = true;
        }).ConfigureAwait(false);
        if (!acknowledged || placement.Selection is not { } selection)
        {
            return;
        }

        await PersistAppliedSelectionAsync(
            selection.ProfileId,
            selection.WorkspaceId,
            selection.SessionId,
            placement.SnapshotGeneration,
            cancellationToken).ConfigureAwait(false);
    }

    private void StartNavigationHighlightLifetime(
        object anchorKey,
        long generation,
        CancellationToken cancellationToken)
    {
        _backgroundTasks.Run(async _ =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1.8), cancellationToken).ConfigureAwait(false);
                await InvokeOnUiThreadAsync(() =>
                {
                    if (!IsCurrentNavigationHighlight(anchorKey, generation, cancellationToken))
                    {
                        return;
                    }
                    foreach (var row in Messages)
                    {
                        if (Equals(row.AnchorKey, anchorKey))
                        {
                            row.FadeNavigationHighlight();
                        }
                    }
                }).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(650), cancellationToken)
                    .ConfigureAwait(false);
                await InvokeOnUiThreadAsync(() =>
                {
                    if (!IsCurrentNavigationHighlight(anchorKey, generation, cancellationToken))
                    {
                        return;
                    }
                    foreach (var row in Messages)
                    {
                        if (Equals(row.AnchorKey, anchorKey))
                        {
                            row.ClearNavigationHighlight();
                        }
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        });
    }

    private bool IsCurrentNavigationHighlight(
        object anchorKey,
        long generation,
        CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested
           && generation == Volatile.Read(ref _navigationHighlightGeneration)
           && Equals(_timeline.SelectedAnchorKey, anchorKey);
}

internal sealed record AgentHistoryNavigationPlacement(
    object AnchorKey,
    int SnapshotGeneration,
    bool UsesLoadedWindow,
    AgentHistorySelection? Selection);

internal sealed record AgentHistorySelection(
    string? ProfileId,
    string? WorkspaceId,
    Guid? SessionId);

internal sealed class AgentHistoryTargetUnavailableException()
    : InvalidOperationException("The selected history result is no longer available.");
