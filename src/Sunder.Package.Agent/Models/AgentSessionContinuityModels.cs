using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Models;

internal enum AgentSessionContextCheckpointKind
{
    Legacy = 0,
    Deterministic = 1,
    ModelRefined = 2,
}

internal sealed record AgentAnchoredSessionContextCheckpoint(
    AgentSessionContextCheckpointRecord Record,
    AgentSessionContextCheckpointKind Kind,
    long TranscriptEpoch,
    DateTimeOffset? CoveredThroughCreatedAtUtc,
    long? CoveredThroughContentRevision,
    AgentDurableRunKey? SourceRun,
    long? SourceRunEpoch,
    long Generation,
    Guid? PreviousContextCheckpointId,
    string? GeneratorVersion,
    string? ProviderId,
    string? ModelId);

internal sealed record AgentSessionContinuitySnapshot(
    Guid SessionId,
    long TranscriptEpoch,
    Guid? ActiveContextCheckpointId,
    long ActiveContextGeneration,
    AgentDurableRunKey SourceRun,
    long SourceRunEpoch,
    Guid SourceUserTurnId,
    IReadOnlyList<AgentTurnRecord> Turns,
    AgentAnchoredSessionContextCheckpoint? ActiveCheckpoint);

internal sealed record AgentSessionContextCheckpointSaveRequest(
    Guid SessionId,
    long TranscriptEpoch,
    Guid? ExpectedActiveContextCheckpointId,
    long ExpectedActiveContextGeneration,
    AgentDurableRunKey SourceRun,
    long SourceRunEpoch,
    Guid FirstOmittedTurnId,
    Guid LastOmittedTurnId,
    int OmittedTurnCount,
    DateTimeOffset CoveredThroughCreatedAtUtc,
    long CoveredThroughContentRevision,
    AgentSessionContextCheckpointKind Kind,
    string SummaryText,
    string DetailsJson,
    string GeneratorVersion,
    string? ProviderId = null,
    string? ModelId = null);
