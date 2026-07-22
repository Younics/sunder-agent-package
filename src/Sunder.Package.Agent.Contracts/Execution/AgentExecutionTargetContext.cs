namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Supplies the persisted workspace binding and caller authorization context for one execution-target operation.
/// </summary>
/// <remarks>
/// The workspace and binding are snapshots used to derive current target configuration. They do not transfer ownership of Runtime state.
/// Targets must canonicalize and revalidate paths at operation time rather than treating this context or an earlier permission classification
/// as proof that a path remains contained.
/// </remarks>
/// <param name="SessionId">
/// The session identity associated with the operation, or <see langword="null"/> for workspace maintenance, readiness checks, or non-session consumers.
/// </param>
/// <param name="ProfileId">The selected profile identity for correlation or profile-scoped policy, or <see langword="null"/> when no profile applies.</param>
/// <param name="Workspace">The selected workspace snapshot whose paths define the configured execution scope.</param>
/// <param name="Binding">
/// The enabled execution binding selecting the target contribution and its binding-scoped configuration. Its workspace identity must match
/// <paramref name="Workspace"/>.
/// </param>
/// <param name="AllowOutsideConfiguredScope">
/// Whether the caller has authorized this request to use paths or working directories outside the configured workspace roots. Setting this
/// flag does not disable canonicalization and does not turn the backend into a security sandbox; callers must set it only after permission approval.
/// </param>
public sealed record AgentExecutionTargetContext(
    Guid? SessionId,
    string? ProfileId,
    AgentWorkspaceRecord Workspace,
    AgentWorkspaceBindingRecord Binding,
    bool AllowOutsideConfiguredScope = false);
