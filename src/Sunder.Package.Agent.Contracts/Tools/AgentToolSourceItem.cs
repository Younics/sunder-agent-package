namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Identifies a source supporting a tool result for transcript citation and presentation.
/// </summary>
/// <remarks>Source data is persisted and may be rendered as a link; consumers must validate URI schemes before navigation.</remarks>
/// <param name="Title">The human-readable source title.</param>
/// <param name="Url">The source URI or canonical reference.</param>
/// <param name="Snippet">An optional bounded excerpt that must be treated as untrusted source content.</param>
public sealed record AgentToolSourceItem(
    string Title,
    string Url,
    string? Snippet = null);
