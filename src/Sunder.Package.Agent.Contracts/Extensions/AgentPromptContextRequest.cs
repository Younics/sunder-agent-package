using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Provides the bounded Runtime state available to a prompt-context contributor for one run.
/// </summary>
/// <remarks>
/// The record and its collections are read-only call snapshots owned by the Runtime; contributors must not
/// mutate them or retain object references beyond the work needed for the call. They can contain private
/// user messages, tool output, model output, workspace identifiers, and derived summaries. Those values
/// retain their original provenance, are not privileged instructions, and must not be logged or sent to an
/// external service without the applicable package policy and user authorization.
/// </remarks>
/// <param name="Session">
/// The current session and profile context snapshot. Its summary and display values can be user-derived.
/// </param>
/// <param name="Run">The current run identity, revision, status, and timing snapshot.</param>
/// <param name="Turn">
/// The current turn context, including the current user message and working summary; both are untrusted input.
/// </param>
/// <param name="Turns">
/// The bounded transcript window selected by the Runtime, in the supplied transcript order. The list can be
/// empty and its records preserve their individual message-role provenance.
/// </param>
/// <param name="RecentLiveBufferTurns">
/// The bounded recent subset still present in live model context. It can overlap <paramref name="Turns"/> and
/// must not be counted as independent evidence merely because it appears in both collections.
/// </param>
/// <param name="ContextPlan">
/// The intent, query, category hints, and contributor-level limits governing this contribution attempt.
/// </param>
public sealed record AgentPromptContextRequest(
    AgentSessionContextRecord Session,
    AgentRunContextRecord Run,
    AgentTurnContextRecord Turn,
    IReadOnlyList<AgentTurnRecord> Turns,
    IReadOnlyList<AgentTurnRecord> RecentLiveBufferTurns,
    AgentPromptContextPlan ContextPlan)
{
    /// <summary>Gets the active profile snapshot when the host can expose it to the contributor.</summary>
    /// <remarks>Profile fields can contain user-authored text and remain non-privileged input data.</remarks>
    public AgentProfileRecord? Profile { get; init; }

    /// <summary>Gets the active workspace snapshot when one is assigned.</summary>
    /// <remarks>Workspace names, descriptions, paths, and documents remain non-privileged input data.</remarks>
    public AgentWorkspaceRecord? Workspace { get; init; }

    /// <summary>Gets the selected execution binding when one is available.</summary>
    public AgentWorkspaceBindingRecord? ExecutionBinding { get; init; }

    /// <summary>Gets the bounded ready-tool snapshot used for this model request.</summary>
    /// <remarks>Descriptor metadata is read-only and does not authorize tool use or context retrieval.</remarks>
    public IReadOnlyList<AgentToolDescriptor> AvailableTools { get; init; } = [];

    /// <summary>
    /// Gets the exact durable rollback receipt that memory contributors must observe before returning context.
    /// </summary>
    /// <remarks>A contributor that cannot verify this barrier must return no rollback-sensitive context rather than block the run.</remarks>
    public AgentMemoryConsistencyBarrier? MemoryConsistencyBarrier { get; init; }

    /// <summary>Gets the current durable transcript epoch used to invalidate rollback-sensitive session context.</summary>
    public long TranscriptEpoch { get; init; }

    /// <summary>Gets an opaque reference to the exact execution-target activation selected for this prompt-context callback.</summary>
    public IPackageExtensionReference<IAgentExecutionTarget>? ExecutionTargetReference { get; init; }
}
