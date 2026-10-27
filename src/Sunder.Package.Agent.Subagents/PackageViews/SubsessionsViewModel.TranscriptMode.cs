using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubsessionsViewModel
{
    private CancellationTokenSource? _transcriptModeCancellation;

    private void ChangeTranscriptMode(bool detailed)
    {
        var anchor = _timeline.CaptureModeAnchor();
        var following = _timeline.IsFollowingLatest;
        _transcriptModeCancellation?.Cancel();
        _transcriptModeCancellation?.Dispose();
        _transcriptModeCancellation = new();
        var token = _transcriptModeCancellation.Token;
        _timeline.SetCompactMode(!detailed);
        _runActivity.Reset();
        if (SelectedSubsession is not { } session) return;
        if (following || anchor is null) { LoadTranscript(session.SessionId, forceReplacement: true); return; }
        _transcriptLoadOperation = _tasks.Track(_ => ChangeAnchoredTranscriptModeAsync(session.SessionId, anchor, token));
    }

    private async Task ChangeAnchoredTranscriptModeAsync(Guid sessionId, TranscriptModeAnchor anchor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_timeline.IsCompact)
        {
            await _timeline.LoadCompactAroundAsync(sessionId, anchor.TurnId, anchor.ItemId, token,
                revealMembers: false, previousModeAnchor: anchor).ConfigureAwait(false);
            return;
        }
        var reader = _transcriptReader ?? throw new InvalidOperationException("The Agent runtime is not available.");
        TranscriptLoadTicket ticket = default;
        await RunOnUiThreadAsync(() => ticket = _timeline.BeginAnchoredInitialLoad(sessionId), token);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, ticket.Generation.CancellationToken);
            var page = await reader.LoadAroundTurnAsync(sessionId, anchor.TurnId, anchor.CreatedAtUtc,
                anchor.ItemId ?? Guid.Empty, 20, 20, linked.Token).ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                var turn = page.Turns.Single(turn => turn.TurnId == anchor.TurnId);
                var item = anchor.ItemId is { } itemId ? turn.Items.Single(item => item.ItemId == itemId) : null;
                var target = item is null ? (object)TranscriptRowAnchorKey.Text(turn.TurnId) : TranscriptRowAnchorKey.Tool(turn, item);
                _timeline.RestoreModeAnchor(anchor, target, notify: false);
                _timeline.TryCompleteAnchoredInitialLoad(ticket, page.Turns, page.HasOlder, page.HasNewer, target,
                    anchor.Viewport is { } viewport ? viewport with { AnchorKey = target } : null);
            }, linked.Token);
        }
        catch
        {
            await RunOnUiThreadAsync(() => _timeline.TryFailInitialLoad(ticket), CancellationToken.None);
            throw;
        }
    }
}
