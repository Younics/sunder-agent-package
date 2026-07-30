using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Supplies the persisted workspace binding and caller authorization context for one execution-target operation.
/// </summary>
/// <remarks>
/// The workspace and binding are snapshots used to derive current target configuration. They do not transfer ownership of Runtime state.
/// Targets must acquire and retain operation-owned filesystem authority at execution time rather than treating this context, a path mapping,
/// or an earlier permission classification as proof that a path remains contained.
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
/// flag does not disable target-owned path validation, is insufficient without exact approved resource references, and does not turn the backend
/// into a security sandbox; callers must set it only after permission approval.
/// </param>
public sealed record AgentExecutionTargetContext(
    Guid? SessionId,
    string? ProfileId,
    AgentWorkspaceRecord Workspace,
    AgentWorkspaceBindingRecord Binding,
    bool AllowOutsideConfiguredScope = false)
{
    /// <summary>Gets opaque, target-defined resource references approved for this exact operation.</summary>
    /// <remarks>This compatibility collection is durable identity data, not authority.</remarks>
    public IReadOnlyList<string> ApprovedResourceReferences { get; init; } = [];

    /// <summary>Gets structured durable resource claims approved for this exact operation.</summary>
    public IReadOnlyList<AgentResourceClaim> ApprovedResourceClaims { get; init; } = [];

    /// <summary>Gets transient process-local capabilities available to this exact operation.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ApprovedResourceCapabilities { get; init; } = [];

    /// <summary>Gets the transient invocation binding used for resource planning or execution.</summary>
    [JsonIgnore]
    public AgentResourceOperationContext? ResourceOperation { get; init; }

    /// <summary>Gets whether a successful mutation should return a transient resource receipt for its exact post-mutation state.</summary>
    [JsonIgnore]
    public bool CapturePostMutationResource { get; init; }

    /// <summary>Gets the target-owned configuration generation captured before permission planning.</summary>
    /// <remarks>When set, the target must reject the operation before side effects if its current generation differs.</remarks>
    [JsonIgnore]
    public string? ExpectedConfigurationGeneration { get; init; }
}
