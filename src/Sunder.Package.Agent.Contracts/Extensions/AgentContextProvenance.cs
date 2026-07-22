namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Identifies the immediate origin of supplementary prompt context.
/// </summary>
/// <remarks>
/// Provenance is independent of <see cref="AgentContextTrust"/> and records origin rather than authority or
/// factual correctness. It is self-declared by the producer and does not replace package ownership, permission,
/// or content validation. Producers should choose the most specific known origin and consumers must tolerate
/// future enum values.
/// </remarks>
public enum AgentContextProvenance
{
    /// <summary>
    /// The immediate source cannot be established.
    /// </summary>
    /// <remarks>Unknown-origin content must use <see cref="AgentContextTrust.Untrusted"/>.</remarks>
    Unknown = 0,

    /// <summary>
    /// The current user supplied the exact content directly.
    /// </summary>
    /// <remarks>
    /// This origin can support <see cref="AgentContextTrust.UserProvided"/>, but does not validate claims or
    /// elevate the content to system-level instruction.
    /// </remarks>
    User = 1,

    /// <summary>
    /// The content is a derived summary or projection of earlier transcript turns.
    /// </summary>
    /// <remarks>Derivation loses direct-user guarantees and must be treated as untrusted reference data.</remarks>
    TranscriptSummary = 2,

    /// <summary>
    /// The content was produced or inferred by an assistant model.
    /// </summary>
    /// <remarks>Model output is untrusted even when generated during a successful run.</remarks>
    Assistant = 3,

    /// <summary>
    /// The content was returned by a tool, command, service, or other external system.
    /// </summary>
    /// <remarks>Installed tool code does not make its external or model-controlled result trusted instruction text.</remarks>
    Tool = 4,

    /// <summary>
    /// The content was assembled by an installed extension without a more specific upstream origin.
    /// </summary>
    /// <remarks>
    /// Package code provenance does not make extension-generated supplementary content privileged; use a trusted
    /// system-prompt contributor only for package-controlled policy.
    /// </remarks>
    Extension = 5,

    /// <summary>
    /// The content was recalled from durable semantic memory or another persisted recall index.
    /// </summary>
    /// <remarks>
    /// Recall can be stale, merged, or derived and must remain untrusted even when its original evidence was a
    /// user turn; retain evidence-level provenance separately when available.
    /// </remarks>
    DurableMemory = 6,
}
