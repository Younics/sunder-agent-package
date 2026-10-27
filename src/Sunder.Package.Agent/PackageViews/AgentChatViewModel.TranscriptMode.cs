using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    private CancellationTokenSource? _transcriptModeCancellation;
    private Task _transcriptModeOperation = Task.CompletedTask;

    private void ChangeTranscriptMode(bool detailed)
    {
        var anchor = _timeline.CaptureModeAnchor();
        var following = _timeline.IsFollowingLatest;
        _transcriptModeCancellation?.Cancel();
        _transcriptModeCancellation?.Dispose();
        _transcriptModeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var token = _transcriptModeCancellation.Token;
        _timeline.SetCompactMode(!detailed);
        _runActivity.Reset();
        if (DisplayedSession is not { } session) return;
        if (following || anchor is null) { RefreshTranscript(forceReplacement: true); return; }
        _transcriptModeOperation = _backgroundTasks.Track(_ => ChangeAnchoredTranscriptModeAsync(session.SessionId, anchor, token));
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
        TranscriptLoadTicket ticket = default;
        await InvokeOnUiThreadAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            ticket = _timeline.BeginAnchoredInitialLoad(sessionId);
        });
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, ticket.Generation.CancellationToken);
            var page = _transcriptAnchorGateway is { } gateway
                ? await gateway.LoadTranscriptAroundTurnAsync(new(sessionId, anchor.TurnId, ItemId: anchor.ItemId), linked.Token).ConfigureAwait(false)
                : await LoadInProcessModeWindowAsync(sessionId, anchor, linked.Token).ConfigureAwait(false);
            await InvokeOnUiThreadAsync(() =>
            {
                linked.Token.ThrowIfCancellationRequested();
                var turn = page.Turns.Single(turn => turn.TurnId == anchor.TurnId);
                var item = anchor.ItemId is { } itemId ? turn.Items.Single(item => item.ItemId == itemId) : null;
                var target = item is null ? (object)TranscriptRowAnchorKey.Text(turn.TurnId) : TranscriptRowAnchorKey.Tool(turn, item);
                _timeline.RestoreModeAnchor(anchor, target, notify: false);
                _timeline.TryCompleteAnchoredInitialLoad(ticket, page.Turns, page.HasOlder, page.HasNewer, target,
                    anchor.Viewport is { } viewport ? viewport with { AnchorKey = target } : null);
            });
        }
        catch
        {
            await InvokeOnUiThreadAsync(() => _timeline.TryFailInitialLoad(ticket));
            throw;
        }
    }

    private Task<AgentTranscriptAroundTurnPage> LoadInProcessModeWindowAsync(Guid sessionId, TranscriptModeAnchor anchor, CancellationToken token)
        => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var headers = _sessionService as IAgentTranscriptHeaderGateway;
            var turn = headers?.GetTranscriptHeader(anchor.TurnId) ?? _sessionService.GetTurn(anchor.TurnId)
                ?? throw new InvalidOperationException("The transcript anchor is no longer available.");
            if (turn.SessionId != sessionId) throw new InvalidOperationException("The transcript anchor belongs to another session.");
            var before = headers?.ListTranscriptHeadersBefore(sessionId, turn.CreatedAtUtc, turn.TurnId, 21)
                ?? _sessionService.ListTurnsBefore(sessionId, turn.CreatedAtUtc, turn.TurnId, 21);
            var after = headers?.ListTranscriptHeadersAfter(sessionId, turn.CreatedAtUtc, turn.TurnId, 21)
                ?? _sessionService.ListTurnsAfter(sessionId, turn.CreatedAtUtc, turn.TurnId, 21);
            return new AgentTranscriptAroundTurnPage(0, [.. before.TakeLast(20), turn, .. after.Take(20)], before.Count > 20, after.Count > 20, turn.TurnId);
        }, token);
}
