global using TranscriptPageCursor = global::Sunder.Package.Agent.Runtime.TranscriptPageCursor;

using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal readonly record struct TranscriptPageCursor(
    DateTimeOffset CreatedAtUtc,
    Guid TurnId)
{
    [Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")]
    public long TurnSequence { get; init; }
    internal static TranscriptPageCursor FromTurn(AgentTurnRecord turn)
        => new(turn.CreatedAtUtc, turn.TurnId) { TurnSequence = turn.TurnSequence };
}
