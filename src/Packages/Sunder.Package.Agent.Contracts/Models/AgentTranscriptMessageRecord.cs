namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentTranscriptMessageRecord(
    Guid MessageId,
    Guid SessionId,
    AgentMessageRole Role,
    string Content,
    DateTimeOffset CreatedAtUtc);
