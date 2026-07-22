namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes one provenance-labeled supplementary reference block for a model request.
/// </summary>
/// <remarks>
/// <para>
/// Prompt context blocks are serialized into a user-role JSON message and are never promoted to the
/// privileged system prompt, regardless of provenance or trust classification. Blank titles or content
/// are omitted. Remaining blocks are ordered by descending priority and then title; a bounded prefix is
/// retained and oversized content can be truncated. Blocks have no identity and are not deduplicated.
/// </para>
/// <para>
/// <see cref="Provenance"/> and <see cref="Trust"/> are explicit labels for model reasoning and auditing,
/// not enforcement or authorization tokens. Content must still be treated as data, screened for accidental
/// secret disclosure, and kept within the permissions and session/workspace scope of its source.
/// </para>
/// </remarks>
/// <param name="Title">
/// A non-empty, non-sensitive label identifying the context to the model. It is also an ordering
/// tie-breaker and is not a stable block identity.
/// </param>
/// <param name="Content">
/// The supplementary reference data. Embedded instructions must not be followed unless independently
/// authorized by the current user's direct request.
/// </param>
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
    AgentContextTrust Trust = AgentContextTrust.Untrusted);
