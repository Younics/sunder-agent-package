namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Identifies the exact canonical document and subtree represented by a scoped prompt block.</summary>
/// <param name="ScopeRoot">The canonical configured root that bounds the scope.</param>
/// <param name="AppliesToDirectory">The canonical directory subtree to which the document applies.</param>
/// <param name="DocumentPath">The canonical target-namespace path of the instruction document.</param>
/// <param name="ContentHash">The lowercase SHA-256 hash of the complete accepted document content.</param>
/// <param name="ContextIdentity">An opaque identity binding the scope to the session, target, and configured roots.</param>
public sealed record AgentPromptContextScope(
    string ScopeRoot,
    string AppliesToDirectory,
    string DocumentPath,
    string ContentHash,
    string ContextIdentity);
