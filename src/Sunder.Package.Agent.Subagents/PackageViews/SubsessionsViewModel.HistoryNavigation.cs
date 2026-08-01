using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubsessionsViewModel
{
    internal static TranscriptNavigationTarget? TryGetNavigationAnchor(
        IReadOnlyDictionary<string, string?> parameters)
        => TranscriptAnchorNavigation.Parse(parameters, requireWorkspace: false);

    public async ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
        => _ = await PrepareNavigationAsync(context, cancellationToken);

    internal async ValueTask<bool> PrepareNavigationAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        var request = _requests.Begin(NavigationChannel, cancellationToken);
        try
        {
            return await OnNavigatedToCoreAsync(context, request).ConfigureAwait(false);
        }
        finally
        {
            _requests.Complete(request);
        }
    }

    private async Task<bool> OnNavigatedToCoreAsync(
        PackageViewNavigationContext context,
        LatestRequestTicket navigation)
    {
        var cancellationToken = navigation.CancellationToken;
        EnsureCurrentNavigation(navigation);
        await RunOnUiThreadAsync(() =>
        {
            EnsureCurrentNavigation(navigation);
            ClearNavigationHighlightForPreparation();
            NavigationAnchorKey = null;
            NavigationSelectionAuthorityRevision = null;
        }, cancellationToken).ConfigureAwait(false);
        var sessionId = TryGetSessionId(context.Parameters);
        var navigationAnchor = TryGetNavigationAnchor(context.Parameters);
        if (navigationAnchor is not null
            && sessionId is not null
            && navigationAnchor.SessionId != sessionId.Value)
        {
            throw new InvalidOperationException("The navigation anchor does not match the selected child session.");
        }
        try
        {
            await EnsureInitializedAsync(
                sessionId,
                cancellationToken,
                suppressTranscriptLoad: navigationAnchor is not null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
            {
                EnsureCurrentNavigation(navigation);
                SetLoadFailure(ex.Message);
            }, cancellationToken).ConfigureAwait(false);
            return sessionId is null && navigationAnchor is null;
        }
        EnsureCurrentNavigation(navigation);
        var navigationAlreadyApplied = false;
        await RunOnUiThreadAsync(() =>
        {
            EnsureCurrentNavigation(navigation);
            navigationAlreadyApplied = sessionId is not null
                                       && SelectedSubsession?.SessionId == sessionId.Value
                                       && _listDetail.IsExistingDetail
                                       && navigationAnchor is null
                                       && _timeline.SessionId == sessionId.Value;
        }, cancellationToken).ConfigureAwait(false);
        if (navigationAlreadyApplied)
        {
            return true;
        }

        if (sessionId is null)
        {
            var defaultTranscriptLoad = Task.CompletedTask;
            await RunOnUiThreadAsync(() =>
            {
                EnsureCurrentNavigation(navigation);
                if (navigationAnchor is null
                    && SelectedSubsession is { } selected
                    && _timeline.SessionId != selected.SessionId)
                {
                    LoadTranscript(selected.SessionId);
                    defaultTranscriptLoad = Volatile.Read(ref _transcriptLoadOperation);
                }
            }, cancellationToken).ConfigureAwait(false);
            await defaultTranscriptLoad.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var reloadRequired = false;
        await RunOnUiThreadAsync(() =>
        {
            EnsureCurrentNavigation(navigation);
            if (FindSubsessionItem(sessionId.Value) is { } subsession)
            {
                RunWithAutomaticTranscriptLoadSuppressed(
                    () => _listDetail.ShowExistingDetail(subsession),
                    suppress: navigationAnchor is not null);
            }
            else
            {
                reloadRequired = true;
            }
        }, cancellationToken).ConfigureAwait(false);
        if (reloadRequired)
        {
            await ReloadSubsessionsAsync(
                    sessionId,
                    cancellationToken,
                    suppressTranscriptLoad: navigationAnchor is not null)
                .ConfigureAwait(false);
        }

        var selectionAuthorityRevision = 0L;
        var transcriptLoadOperation = Task.CompletedTask;
        await RunOnUiThreadAsync(() =>
        {
            EnsureCurrentNavigation(navigation);
            if (SelectedSubsession?.SessionId != sessionId.Value
                || !_knownSessions.TryGetValue(sessionId.Value, out var selectedSession)
                || selectedSession.ParentSessionId is null)
            {
                throw new InvalidOperationException("The requested child session is no longer available.");
            }

            selectionAuthorityRevision = IntentRevision;
            if (navigationAnchor is null && _timeline.SessionId != sessionId.Value)
            {
                LoadTranscript(sessionId.Value);
            }
            transcriptLoadOperation = navigationAnchor is null
                ? Volatile.Read(ref _transcriptLoadOperation)
                : Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        await transcriptLoadOperation.WaitAsync(cancellationToken).ConfigureAwait(false);
        await RunOnUiThreadAsync(() =>
        {
            EnsureCurrentAnchorSelection(
                sessionId.Value,
                navigation,
                selectionAuthorityRevision,
                cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
        if (navigationAnchor is not null)
        {
            await NavigateToAnchorAsync(
                navigationAnchor,
                navigation,
                selectionAuthorityRevision,
                cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    private void EnsureCurrentNavigation(LatestRequestTicket navigation)
    {
        navigation.CancellationToken.ThrowIfCancellationRequested();
        if (!_requests.IsCurrent(navigation))
        {
            throw new OperationCanceledException("The subsession navigation was superseded.");
        }
    }

    private async Task NavigateToAnchorAsync(
        TranscriptNavigationTarget target,
        LatestRequestTicket navigation,
        long selectionAuthorityRevision,
        CancellationToken cancellationToken)
    {
        EnsureCurrentNavigation(navigation);
        var reader = _transcriptReader
            ?? throw new InvalidOperationException("The Agent runtime is not available.");
        var page = await reader.LoadAroundTurnAsync(
            target.SessionId,
            target.TurnId,
            target.CreatedAtUtc,
            target.ItemId,
            beforeLimit: 20,
            afterLimit: 20,
            cancellationToken).ConfigureAwait(false);
        if (page.AnchorTurnId != target.TurnId
            || !TryResolveAnchorKey(target, page.Turns, out var anchorKey))
        {
            throw new SubsessionHistoryTargetUnavailableException(target.AnchorKind);
        }
        await RunOnUiThreadAsync(() =>
        {
            EnsureCurrentAnchorSelection(
                target.SessionId,
                navigation,
                selectionAuthorityRevision,
                cancellationToken);
            var ticket = _timeline.BeginAnchoredInitialLoad(target.SessionId);
            if (!_timeline.TryCompleteAnchoredInitialLoad(
                    ticket,
                    page.Turns,
                    page.HasOlder,
                    page.HasNewer,
                    anchorKey))
            {
                throw new InvalidOperationException("The transcript navigation was superseded.");
            }
            NavigationAnchorKey = anchorKey;
            NavigationSelectionAuthorityRevision = selectionAuthorityRevision;
            _knownCheckpoints.TryGetValue(target.SessionId, out var checkpoint);
            _runActivity.TrackCheckpoint(checkpoint);
            ApplyRunActivityState();
        }, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureCurrentAnchorSelection(
        Guid targetSessionId,
        LatestRequestTicket navigation,
        long selectionAuthorityRevision,
        CancellationToken cancellationToken)
    {
        EnsureCurrentNavigation(navigation);
        if (selectionAuthorityRevision != IntentRevision
            || SelectedSubsession?.SessionId != targetSessionId
            || !_knownSessions.TryGetValue(targetSessionId, out var selected)
            || selected.ParentSessionId is null)
        {
            throw new OperationCanceledException("The subsession selection superseded transcript navigation.");
        }
    }

    internal bool IsCurrentNavigationPlacement(Guid targetSessionId)
        => NavigationAnchorKey is not null
           && NavigationSelectionAuthorityRevision == IntentRevision
           && SelectedSubsession?.SessionId == targetSessionId;

    private static bool TryResolveAnchorKey(
        TranscriptNavigationTarget target,
        IReadOnlyList<Sunder.Package.Agent.Contracts.Models.AgentTurnRecord> turns,
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
                                       && item.Kind == Sunder.Package.Agent.Contracts.Models.AgentTurnItemKind.Text))
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

    internal void ClearNavigationHighlightForPreparation()
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

    internal void AcknowledgeHistoryPresentation(
        Guid targetSessionId,
        object anchorKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentNavigationPlacement(targetSessionId)
            || !Equals(NavigationAnchorKey, anchorKey))
        {
            return;
        }

        _navigationHighlightCancellation?.Cancel();
        _navigationHighlightCancellation?.Dispose();
        _navigationHighlightCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var generation = Interlocked.Increment(ref _navigationHighlightGeneration);
        var token = _navigationHighlightCancellation.Token;
        foreach (var row in Messages)
        {
            if (Equals(row.AnchorKey, anchorKey))
            {
                row.PrimeNavigationHighlight();
            }
            else
            {
                row.ClearNavigationHighlight();
            }
        }
        _tasks.Run(async _ =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1.8), token).ConfigureAwait(false);
                await RunOnUiThreadAsync(() =>
                {
                    if (!IsCurrentNavigationHighlight(anchorKey, generation, token))
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
                }, token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(650), token).ConfigureAwait(false);
                await RunOnUiThreadAsync(() =>
                {
                    if (!IsCurrentNavigationHighlight(anchorKey, generation, token))
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
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
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
           && Equals(NavigationAnchorKey, anchorKey);
}

internal sealed class SubsessionHistoryTargetUnavailableException(
    TranscriptNavigationAnchorKind anchorKind)
    : InvalidOperationException(anchorKind == TranscriptNavigationAnchorKind.Activity
        ? "The activity anchor is no longer available."
        : "The selected history result is no longer available.");
