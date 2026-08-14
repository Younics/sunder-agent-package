using Sunder.Package.Agent.Memory.Semantic.Services;

namespace Sunder.Package.Agent.Memory.Semantic;

public static class MemoryRecordStates
{
    public const string Active = "Active";
    public const string Contested = "Contested";
    public const string Forgotten = "Forgotten";
    public const string Superseded = "Superseded";
}

public sealed record SemanticMemoryStatusRecord(string StatusText, bool CanReindex);

public sealed record SemanticMemorySessionStateRecord(
    SemanticEmbeddingContext Context,
    SemanticMemoryStatusRecord Status);

public sealed record SemanticMemoryReindexResult(string Message, int IndexedMemoryCount);

public sealed record SemanticMemoryWorkerStatusRecord(
    string StatusText,
    bool IsRunning,
    int PendingItemCount,
    long ProcessedItemCount,
    DateTimeOffset? LastSuccessfulRunAtUtc,
    DateTimeOffset? LastFailureAtUtc,
    string? LastFailureMessage,
    bool HasFailure);

public sealed record MemorySemanticIndexStatusRecord(
    string StatusLabel,
    string StatusText,
    SemanticMemoryEntryIndexState? IndexState = null,
    string? ProviderDisplayName = null,
    string? ModelId = null);
