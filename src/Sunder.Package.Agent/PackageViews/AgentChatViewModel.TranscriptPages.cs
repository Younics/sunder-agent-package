using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    private Task<AgentTranscriptPage> LoadTranscriptPageAsync(AgentTranscriptPageRequest request, CancellationToken cancellationToken)
    {
        if (_transcriptPageGateway is not null)
            return _transcriptPageGateway.LoadTranscriptPageAsync(request, cancellationToken);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var limit = Math.Clamp(request.Limit, 1, 500);
            var headers = _sessionService as IAgentTranscriptHeaderGateway;
            IReadOnlyList<AgentTurnRecord> turns = request.Direction switch
            {
                AgentTranscriptPageDirection.Recent => headers?.ListRecentTranscriptHeaders(request.SessionId, limit + 1)
                    ?? Project(_sessionService.ListRecentTurns(request.SessionId, limit + 1)),
                AgentTranscriptPageDirection.Before when request.AnchorCreatedAtUtc is { } createdAt && request.AnchorTurnId is { } turnId
                    => headers?.ListTranscriptHeadersBefore(request.SessionId, createdAt, turnId, limit + 1)
                       ?? Project(_sessionService.ListTurnsBefore(request.SessionId, createdAt, turnId, limit + 1)),
                AgentTranscriptPageDirection.After when request.AnchorCreatedAtUtc is { } createdAt && request.AnchorTurnId is { } turnId
                    => headers?.ListTranscriptHeadersAfter(request.SessionId, createdAt, turnId, limit + 1)
                       ?? Project(_sessionService.ListTurnsAfter(request.SessionId, createdAt, turnId, limit + 1)),
                AgentTranscriptPageDirection.Turn when request.AnchorTurnId is { } turnId
                    => (headers is null ? _sessionService.GetTurn(turnId) : headers.GetTranscriptHeader(turnId)) is { } turn
                        ? [TranscriptTurnTransportProjection.ProjectToolHeaders(turn)] : [],
                _ => throw new InvalidOperationException("The transcript page anchor is invalid."),
            };
            cancellationToken.ThrowIfCancellationRequested();
            var more = turns.Count > limit;
            var selected = !more ? turns
                : request.Direction is AgentTranscriptPageDirection.Recent or AgentTranscriptPageDirection.Before
                    ? turns.TakeLast(limit).ToArray() : turns.Take(limit).ToArray();
            return new AgentTranscriptPage(0, selected, more);
        }, cancellationToken);
    }

    private static AgentTurnRecord[] Project(IReadOnlyList<AgentTurnRecord> turns)
        => turns.Select(TranscriptTurnTransportProjection.ProjectToolHeaders).ToArray();
}
