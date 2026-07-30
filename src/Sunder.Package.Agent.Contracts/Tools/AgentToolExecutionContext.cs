using System.Text.Json.Serialization;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Carries host-verified resource scope and durable-run identity into tool execution and permission planning.
/// </summary>
/// <remarks>
/// Context records are host-owned snapshots and should not be retained or modified. Correlation
/// identifiers are not authorization by themselves. Implementations must still validate tool
/// arguments and re-resolve resources at execution time.
/// </remarks>
/// <param name="SessionId">The active session identifier, or <see langword="null" /> outside a durable session.</param>
/// <param name="ProfileId">The selected profile identifier, or <see langword="null" /> when no profile is in scope.</param>
/// <param name="Workspace">The selected workspace snapshot, or <see langword="null" /> for non-workspace tools.</param>
/// <param name="ExecutionBinding">The selected workspace execution binding, or <see langword="null" /> when none applies.</param>
/// <param name="AllowOutsideConfiguredScope">Whether the host has approved access outside configured workspace paths for this invocation; targets must still resolve and enforce the approved boundary.</param>
/// <param name="RunId">The durable run identifier used for correlation and approval binding.</param>
/// <param name="RunRevision">The durable run revision that must remain current for execution.</param>
/// <param name="UserTurnId">The user turn that initiated the provider run.</param>
/// <param name="ToolCallId">The provider's call identifier used to correlate approval and result records.</param>
public sealed record AgentToolExecutionContext(
    Guid? SessionId,
    string? ProfileId = null,
    AgentWorkspaceRecord? Workspace = null,
    AgentWorkspaceBindingRecord? ExecutionBinding = null,
    bool AllowOutsideConfiguredScope = false,
    Guid? RunId = null,
    long? RunRevision = null,
    Guid? UserTurnId = null,
    string? ToolCallId = null)
{
    /// <summary>Gets the current durable transcript epoch for session-owned tool context.</summary>
    public long TranscriptEpoch { get; init; }

    /// <summary>Gets opaque canonical resource references approved for this exact invocation.</summary>
    public IReadOnlyList<string> ApprovedResourceReferences { get; init; } = [];

    /// <summary>Gets the host-verified durable resource claims approved for this exact invocation.</summary>
    public IReadOnlyList<AgentResourceClaim> ApprovedResourceClaims { get; init; } = [];

    /// <summary>Gets transient process-local capabilities for this exact invocation.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ApprovedResourceCapabilities { get; init; } = [];

    /// <summary>Gets the transient invocation binding used to issue or redeem resource authority.</summary>
    [JsonIgnore]
    public AgentResourceOperationContext? ResourceOperation { get; init; }

    /// <summary>Gets an opaque reference to the exact execution-target activation selected when this tool was advertised.</summary>
    public IPackageExtensionReference<IAgentExecutionTarget>? ExecutionTargetReference { get; init; }

    /// <summary>Gets the target-owned configuration generation captured for permission planning and execution.</summary>
    /// <remarks>Target-backed tools must propagate this value to <see cref="AgentExecutionTargetContext.ExpectedConfigurationGeneration"/>.</remarks>
    [JsonIgnore]
    public string? ExecutionTargetConfigurationGeneration { get; init; }
}
