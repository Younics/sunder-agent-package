namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Captures one immutable recall snapshot, including the evidence and labels needed to decide how it may be used.
/// </summary>
/// <remarks>
/// A recall entry is reference data, not a privileged instruction. In particular, pinning and a high
/// relevance score do not increase its authority. The record does not clone <paramref name="MatchReasons"/>;
/// callers and producers must treat the supplied collection as immutable.
/// </remarks>
/// <param name="MemoryId">An opaque, stable identifier for the durable memory. Consumers must not depend on the built-in store's identifier format.</param>
/// <param name="Category">The implementation-defined category used to group and filter memories.</param>
/// <param name="Content">The recalled assertion or reference text.</param>
/// <param name="EvidenceText">Source evidence retained with the memory, or <see langword="null"/> when no evidence was captured. Evidence remains untrusted input.</param>
/// <param name="Score">An implementation-defined ranking score relative to this recall operation; it is not a probability and has no portable range.</param>
/// <param name="IsPinned">Whether the memory receives pinned-item retrieval priority. This does not imply user authorship or trust.</param>
/// <param name="TrustState">The current usage classification derived from provenance, category, and conflict state.</param>
/// <param name="SourceTurnId">The persisted transcript turn from which the memory originated, or <see langword="null"/> when unavailable.</param>
/// <param name="MatchReasons">Diagnostic explanations for selection and ranking, or <see langword="null"/> when the provider does not expose them.</param>
/// <param name="Provenance">The party that originally supplied the remembered assertion, independently of its current trust classification.</param>
public sealed record AgentMemoryRecallEntry(
    string MemoryId,
    string Category,
    string Content,
    string? EvidenceText,
    float Score,
    bool IsPinned,
    AgentMemoryTrustState TrustState = AgentMemoryTrustState.Untrusted,
    Guid? SourceTurnId = null,
    IReadOnlyList<AgentMemoryMatchReason>? MatchReasons = null,
    AgentMemoryProvenance Provenance = AgentMemoryProvenance.Unknown);
