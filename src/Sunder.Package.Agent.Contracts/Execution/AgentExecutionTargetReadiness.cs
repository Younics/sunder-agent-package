namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Reports a point-in-time readiness assessment for one execution target and workspace context.
/// </summary>
/// <remarks>
/// Readiness neither reserves backend resources nor grants permission, and it can become stale immediately after the check. The target
/// identities must match the descriptor of the contribution that produced the result.
/// </remarks>
/// <param name="TargetKind">The stable backend category from <see cref="AgentExecutionTargetDescriptor.TargetKind"/>.</param>
/// <param name="TargetId">The stable contribution identity from <see cref="AgentExecutionTargetDescriptor.TargetId"/>.</param>
/// <param name="Status">The operational classification for the supplied workspace binding.</param>
/// <param name="Message">
/// A user-facing explanation or remediation hint. Producers must avoid including credentials, environment secrets, or unnecessarily sensitive paths.
/// </param>
public sealed record AgentExecutionTargetReadiness(
    string TargetKind,
    string TargetId,
    AgentExecutionTargetReadinessStatus Status,
    string Message);
