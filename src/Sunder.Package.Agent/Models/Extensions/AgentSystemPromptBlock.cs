namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes one trusted, package-controlled instruction block for the privileged system prompt.
/// </summary>
/// <remarks>
/// <para>
/// The Runtime rejects blank identifiers, titles, and content. It deduplicates blocks
/// case-insensitively by (source identifier, block identifier); when keys collide, a required block
/// wins, followed by higher priority and then title. Rendering orders required blocks first, followed
/// by descending priority and stable textual tie-breakers. Consumers must not rely on input-list order.
/// </para>
/// <para>
/// This type crosses a privilege boundary: <see cref="Content"/> becomes system-role text. Only static
/// or package-controlled policy belongs here. User messages, transcripts, memories, tool or model output,
/// workspace files, and remote content remain untrusted regardless of which package read them and must be
/// represented by <see cref="AgentPromptContextBlock"/> instead. Metadata is diagnostic only, does not
/// authorize content, and must not contain credentials or other secrets.
/// </para>
/// <para>
/// The record is a call snapshot. Its metadata dictionary is not copied, so producers must not mutate it
/// after return and consumers must treat it as read-only.
/// </para>
/// </remarks>
/// <param name="BlockId">
/// The stable identifier within <paramref name="SourceId"/>. Matching is ordinal case-insensitive during
/// Runtime deduplication.
/// </param>
/// <param name="Title">
/// The non-empty diagnostic label rendered as the block heading and used as an ordering tie-breaker.
/// It must be package-controlled and must not contain sensitive data.
/// </param>
/// <param name="Content">
/// The non-empty trusted instruction text. It must not include or interpolate untrusted request data.
/// </param>
/// <param name="Priority">
/// The relative ordering priority among blocks with the same required status; higher values render first.
/// Priority is not a trust level or authorization mechanism.
/// </param>
/// <param name="Required">
/// Whether prompt-budget logic should retain this block ahead of optional blocks. This flag does not make
/// untrusted content safe for the system prompt.
/// </param>
/// <param name="MaxChars">
/// An optional positive character limit applied to trimmed content. A missing or non-positive value leaves
/// the block without a per-block limit; producers should still keep instructions bounded.
/// </param>
/// <param name="SourceId">
/// The stable contributing-source identity, normally
/// <see cref="Contracts.IAgentSystemPromptContributor.ContributorId"/>. A missing value places the block
/// in a shared empty-source deduplication namespace and is therefore discouraged for package contributions.
/// </param>
/// <param name="Metadata">
/// Optional read-only diagnostic metadata. It is not rendered as instruction content by the Agent composer
/// and must not be used to carry secrets or security decisions.
/// </param>
public sealed record AgentSystemPromptBlock(
    string BlockId,
    string Title,
    string Content,
    int Priority = 0,
    bool Required = false,
    int? MaxChars = null,
    string? SourceId = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
