using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.PackageViews;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal readonly record struct TranscriptPageCursor(
    DateTimeOffset CreatedAtUtc,
    Guid TurnId)
{
    [Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")]
    public long TurnSequence { get; init; }
    internal static TranscriptPageCursor FromTurn(AgentTurnRecord turn)
        => turn.CreatedAtUtc.TryGetDateTimeOffset(out var timestamp) ? new(timestamp, turn.TurnId) { TurnSequence = turn.TurnSequence }
            : throw new InvalidDataException("The transcript timestamp is not exactly representable.");
}
