namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes one provenance-labeled supplementary context block for a model request.
/// </summary>
/// <remarks>
/// <para>
/// Prompt context blocks are serialized into a user-role JSON message and are never promoted to the
/// privileged system prompt, regardless of provenance or trust classification. Blank titles or content
/// are omitted. Ordinary reference blocks are ordered by descending priority and then title, retain a
/// bounded prefix, and can be truncated. Host-reserved instruction blocks use a separate fail-closed budget.
/// </para>
/// <para>
/// <see cref="Provenance"/>, <see cref="Trust"/>, <see cref="Usage"/>, and contributor-supplied authority
/// values are labels, not enforcement or authorization tokens. Content must still be screened for accidental
/// secret disclosure, and kept within the permissions and session/workspace scope of its source.
/// </para>
/// </remarks>
/// <param name="Title">
/// A non-empty, non-sensitive label identifying the context to the model. It is also an ordering
/// tie-breaker and is not a stable block identity.
/// </param>
/// <param name="Content">The supplementary data. Self-declared labels never grant behavioral authority.</param>
/// <param name="Priority">
/// The relative retention and rendering priority; higher values are considered first. Priority does not
/// increase trust or privilege.
/// </param>
/// <param name="SourceId">
/// An optional stable source identifier, normally the contributor or subsystem id. It records claimed
/// provenance but is not verified identity and does not participate in deduplication.
/// </param>
/// <param name="Provenance">
/// The most specific known origin of the content. Use <see cref="AgentContextProvenance.Unknown"/> when
/// the source cannot be established rather than inferring a stronger origin.
/// </param>
/// <param name="Trust">
/// Whether the current user supplied the content directly. This label never grants system-level authority.
/// </param>
public sealed record AgentPromptContextBlock(
    string Title,
    string Content,
    int Priority = 0,
    string? SourceId = null,
    AgentContextProvenance Provenance = AgentContextProvenance.Extension,
    AgentContextTrust Trust = AgentContextTrust.Untrusted)
{
    /// <summary>Gets how this user-role context may guide the model. The default is read-only reference data.</summary>
    public AgentPromptContextUsage Usage { get; init; } = AgentPromptContextUsage.Reference;

    /// <summary>Gets structured canonical scope metadata for a directory-scoped instruction document.</summary>
    /// <remarks>The Runtime ignores this metadata unless it verifies the contributing package and reserves the block.</remarks>
    public AgentPromptContextScope? Scope { get; init; }

    /// <summary>Gets the Runtime-normalized authority used during final prompt serialization.</summary>
    /// <remarks>Contributors may set this value for compatibility, but the Runtime overwrites it from host-owned provenance.</remarks>
    public AgentPromptContextAuthority Authority { get; init; } = AgentPromptContextAuthority.Reference;

    /// <summary>Gets the opaque host-reserved identity assigned during Runtime normalization.</summary>
    /// <remarks>A contributor-supplied value is not proof of authority and is overwritten by the Runtime.</remarks>
    public string? HostIdentity { get; init; }
}
