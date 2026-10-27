namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>The unit counted by Compact activity pagination.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentCompactTranscriptEntry(
    Guid EntryId,
    DateTimeOffset CreatedAtUtc,
    Guid TurnId,
    AgentTurnRecord? Message,
    AgentTranscriptActivityGroup? Group)
{
    [Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")]
    public long TurnSequence { get; init; }
}

/// <summary>A position in invocation order, independent of result arrival order.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentTranscriptInvocationCursor(
    DateTimeOffset CreatedAtUtc,
    Guid TurnId,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] int SequenceNumber,
    Guid ItemId)
{
    [Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")]
    public long TurnSequence { get; init; }
}

/// <summary>A complete group's metadata. Individual invocation headers are loaded separately.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentTranscriptActivityGroup(
    Guid SessionId,
    AgentTranscriptInvocationCursor First,
    AgentTranscriptInvocationCursor Last,
    Guid? RunId,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] long? RunRevision,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "1")] long TranscriptEpoch,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "1")] long Revision,
    IReadOnlyList<AgentTranscriptToolCount> Tools,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] long RunningCount,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] long CompletedCount,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] long FailedCount);

/// <summary>Invocation count for one tool, ordered by its first occurrence in the group.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentTranscriptToolCount(
    string ToolId,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "1")] long Count);

/// <summary>Compact pages retain complete groups even when they contain many invocations.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum AgentCompactTranscriptDirection { Recent = 0, Before = 1, After = 2, Around = 3, Window = 4 }

/// <summary>Reads Compact entries. Window refreshes retain the reader's existing chronological range.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentCompactTranscriptRequest(
    Guid SessionId,
    AgentCompactTranscriptDirection Direction,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "1", Maximum = "500")] int Limit = 45,
    DateTimeOffset? AnchorCreatedAtUtc = null,
    Guid? AnchorTurnId = null,
    Guid? AnchorItemId = null,
    DateTimeOffset? EndCreatedAtUtc = null,
    Guid? EndTurnId = null);

/// <summary>Older/newer flags describe entries outside this page, not hidden group members.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentCompactTranscriptPage(
    IReadOnlyList<AgentCompactTranscriptEntry> Entries,
    bool HasOlder,
    bool HasNewer,
    Guid? AnchorEntryId = null)
{
    [Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")]
    public long TranscriptEpoch { get; init; }
}

/// <summary>Loads a finite opening snapshot of a group in bounded header-only chunks.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentTranscriptActivityMembersRequest(
    Guid SessionId,
    Guid FirstItemId,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "1")] long TranscriptEpoch,
    AgentTranscriptInvocationCursor Through,
    AgentTranscriptInvocationCursor? After = null,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "1", Maximum = "200")] int Limit = 100);

/// <summary>One canonical header per invocation; arguments and results remain lazy.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentTranscriptActivityMembersPage(
    IReadOnlyList<AgentTurnRecord> Headers,
    bool HasMore,
    AgentTranscriptInvocationCursor? Continuation);
