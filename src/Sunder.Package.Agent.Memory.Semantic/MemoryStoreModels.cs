using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Memory.Semantic;

public sealed record StoredMemoryRecord(
    Guid MemoryId,
    Guid SessionId,
    string Category,
    string Content,
    string? EvidenceText,
    Guid? SourceTurnId,
    float Importance,
    float Confidence,
    bool IsPinned,
    string State,
    Guid? SupersededByMemoryId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastAccessedAtUtc,
    int AccessCount,
    AgentMemoryProvenance Provenance = AgentMemoryProvenance.Unknown)
{
    public long MemoryRevision { get; init; } = 1;

    public bool IsManual { get; init; } = true;
}

public sealed record MemoryCorrectionResult(
    StoredMemoryRecord CorrectedMemory,
    StoredMemoryRecord UpdatedSourceMemory,
    bool CreatedNewMemory);

public sealed record MemoryUpsertRequest(
    Guid SessionId,
    string Category,
    string Content,
    string NormalizedContent,
    string? EvidenceText,
    Guid? SourceTurnId,
    bool IsPinned,
    float Importance,
    float Confidence,
    AgentMemoryProvenance Provenance = AgentMemoryProvenance.Unknown);

public sealed record StoredMemoryEvidenceRecord(
    Guid EvidenceId,
    Guid MemoryId,
    Guid SessionId,
    Guid? SourceTurnId,
    string? EvidenceText,
    DateTimeOffset CreatedAtUtc)
{
    public string? ContributionId { get; init; }
}

public sealed record StoredMemoryEmbeddingRecord(
    Guid MemoryId,
    Guid SessionId,
    string ProviderId,
    string ModelId,
    string CanonicalTextHash,
    int Dimensions,
    IReadOnlyList<float> Values,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public long MemoryRevision { get; init; }
}

internal sealed record StoredMemoryEmbeddingGenerationRecord(
    string GenerationId,
    Guid SessionId,
    string ProviderId,
    string ModelId,
    string? ConfigurationFingerprint,
    string? SourceFingerprint,
    int? MaxCanonicalTextChars,
    int ExpectedMemoryCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record StoredMemorySearchResult(
    StoredMemoryRecord Memory,
    double SearchRank);
