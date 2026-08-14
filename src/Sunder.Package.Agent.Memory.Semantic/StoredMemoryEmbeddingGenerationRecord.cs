namespace Sunder.Package.Agent.Memory.Semantic;

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
