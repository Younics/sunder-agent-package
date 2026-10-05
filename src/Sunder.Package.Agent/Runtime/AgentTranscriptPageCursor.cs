using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal readonly record struct TranscriptPageCursor(
    DateTimeOffset CreatedAtUtc,
    Guid TurnId)
{
    internal static TranscriptPageCursor FromTurn(AgentTurnRecord turn)
        => new(turn.CreatedAtUtc, turn.TurnId);
}
