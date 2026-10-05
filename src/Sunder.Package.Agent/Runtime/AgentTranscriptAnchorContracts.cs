using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Runtime;

internal interface IAgentTranscriptAnchorGateway
{
    Task<AgentTranscriptAroundTurnPage> LoadTranscriptAroundTurnAsync(
        AgentTranscriptAroundTurnRequest request,
        CancellationToken cancellationToken = default);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentTranscriptAroundTurnRequest(
    Guid SessionId,
    Guid TurnId,
    int BeforeLimit = 20,
    int AfterLimit = 20,
    Guid? ItemId = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record AgentTranscriptAroundTurnPage(
    long Revision,
    IReadOnlyList<AgentTurnRecord> Turns,
    bool HasOlder,
    bool HasNewer,
    Guid AnchorTurnId);
