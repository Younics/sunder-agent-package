using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Runtime;

internal interface IAgentTranscriptAnchorGateway
{
    Task<AgentTranscriptAroundTurnPage> LoadTranscriptAroundTurnAsync(
        AgentTranscriptAroundTurnRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed record AgentTranscriptAroundTurnRequest(
    Guid SessionId,
    Guid TurnId,
    int BeforeLimit = 20,
    int AfterLimit = 20,
    Guid? ItemId = null);

internal sealed record AgentTranscriptAroundTurnPage(
    long Revision,
    IReadOnlyList<AgentTurnRecord> Turns,
    bool HasOlder,
    bool HasNewer,
    Guid AnchorTurnId);
