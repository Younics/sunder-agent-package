using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Exposes the bounded, run-fenced host operations available to an Agent behavior loop.
/// </summary>
/// <remarks>
/// Returned records and collections are immutable snapshots. The host persists transcript and checkpoint mutations
/// behind a durable lease epoch that is intentionally not exposed here; even a positive <see cref="IsCurrentRun"/>
/// check is advisory, and a later mutation can still be rejected if ownership changes concurrently.
/// </remarks>
public interface IAgentBehaviorLoopRuntime
{
    /// <summary>Tests whether this runtime still represents the active run identifier and per-session run revision.</summary>
    /// <returns><see langword="true"/> when the run is currently registered as active; otherwise <see langword="false"/>.</returns>
    bool IsCurrentRun();

    /// <summary>Lists all persisted session turns available to the run in transcript order.</summary>
    /// <returns>A detached snapshot collection that the caller must not mutate.</returns>
    IReadOnlyList<AgentTurnRecord> ListTurns();

    /// <summary>Lists up to the requested number of newest persisted turns.</summary>
    /// <param name="limit">The maximum number of turns; the base host treats non-positive values as zero.</param>
    /// <returns>Detached turn snapshots in ascending transcript order.</returns>
    IReadOnlyList<AgentTurnRecord> ListRecentTurns(int limit);

    /// <summary>Builds host-authorized instructions and separately labeled lower-trust reference context.</summary>
    /// <param name="cancellationToken">A token that cancels context contributor, memory, and persistence work.</param>
    /// <returns>The trust-separated instruction snapshot for the next model request.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    ValueTask<AgentBehaviorInstructionContext> BuildInstructionContextAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolves tools that are assigned, ready, and eligible to be advertised for the current run snapshot.</summary>
    /// <param name="cancellationToken">A token that cancels capability and readiness discovery.</param>
    /// <returns>An immutable-by-contract snapshot collection of executable tool adapters.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    ValueTask<IReadOnlyList<AgentRuntimeTool>> ListReadyToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a chat client from the provider/model binding selected for this run.</summary>
    /// <param name="context">The provider request context; the host augments correlation attributes with session, run, revision, and profile identifiers.</param>
    /// <param name="cancellationToken">A token that cancels provider client creation.</param>
    /// <returns>The configured provider chat client.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    ValueTask<IChatClient> CreateChatClientAsync(
        AgentChatClientContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically advances the current durable run and appends a checkpoint.</summary>
    /// <remarks>
    /// The base host permits running progress checkpoints and legal terminal transitions from a running run.
    /// Approval suspension is coordinated by tool invocation rather than by directly saving
    /// <see cref="AgentRunStatus.WaitingForApproval"/>. A terminal transition also closes open streaming turns.
    /// </remarks>
    /// <param name="status">The next legal lifecycle status for this run revision.</param>
    /// <param name="summary">Optional human-readable progress or failure detail; it is not a stable error-code field.</param>
    /// <returns>The newly persisted checkpoint, or the already-persisted terminal checkpoint when a concurrent terminal transition won.</returns>
    /// <exception cref="InvalidOperationException">The requested transition is stale or illegal and no terminal checkpoint can reconcile it.</exception>
    AgentRunCheckpointRecord SaveCheckpoint(AgentRunStatus status, string? summary);

    /// <summary>Submits a structured, run-correlated package event without affecting execution if logging fails.</summary>
    /// <param name="level">The event severity.</param>
    /// <param name="eventName">The stable event name.</param>
    /// <param name="message">The human-readable event message.</param>
    /// <param name="elapsedMilliseconds">Optional elapsed time associated with the event, conventionally non-negative.</param>
    /// <param name="attributes">Optional structured attributes. The base host copies these before asynchronous logging.</param>
    /// <param name="exception">An optional exception retained for diagnostics rather than rethrown by the logger.</param>
    void LogEvent(
        PackageLogLevel level,
        string eventName,
        string message,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null);

    /// <summary>Creates or revisionally updates the assistant message receiving streamed text.</summary>
    /// <remarks>
    /// <paramref name="content"/> is the complete projection, not merely a delta. The host emits an append mutation
    /// when it extends the prior text and a replacement snapshot otherwise.
    /// </remarks>
    /// <param name="assistantTurn">The newest snapshot previously returned for this open turn, or <see langword="null"/> to create one.</param>
    /// <param name="content">The complete assistant text to persist for the next content revision.</param>
    /// <returns>The newly persisted streaming snapshot with an advanced content revision.</returns>
    /// <exception cref="InvalidOperationException">Run ownership, lease epoch, or turn correlation became stale before the mutation committed.</exception>
    AgentTurnRecord UpsertAssistantTurn(AgentTurnRecord? assistantTurn, string content);

    /// <summary>Closes a streamed assistant turn and advances its content revision without changing text.</summary>
    /// <remarks>
    /// Host implementations persist the completion. The default interface implementation returns the supplied
    /// snapshot unchanged for compatibility, so loops should call this method but must not assume every custom
    /// runtime changes <see cref="AgentTurnRecord.IsStreaming"/>.
    /// </remarks>
    /// <param name="assistantTurn">The newest snapshot of the open assistant turn.</param>
    /// <returns>The completed persisted snapshot, or the unchanged argument from the default implementation.</returns>
    /// <exception cref="InvalidOperationException">A host implementation rejects stale run or turn ownership.</exception>
    AgentTurnRecord CompleteAssistantTurn(AgentTurnRecord assistantTurn) => assistantTurn;

    /// <summary>Publishes a bounded lifecycle snapshot to registered extension observers.</summary>
    /// <param name="kind">The lifecycle event kind.</param>
    /// <param name="status">The current run status.</param>
    /// <param name="triggerTurn">The persisted turn associated with the event, or <see langword="null"/>.</param>
    /// <param name="checkpoint">The persisted checkpoint associated with the event, or <see langword="null"/>.</param>
    /// <param name="isInterrupted">Whether the event represents interrupted rather than normally completed work.</param>
    /// <param name="cancellationToken">A token that stops observer publication. The base host isolates non-cancellation failures from optional observers.</param>
    /// <returns>An awaitable that completes after observers have been offered the event.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    ValueTask PublishLifecycleEventAsync(
        AgentLifecycleEventKind kind,
        AgentRunStatus status,
        AgentTurnRecord? triggerTurn = null,
        AgentRunCheckpointRecord? checkpoint = null,
        bool isInterrupted = false,
        CancellationToken cancellationToken = default);

    /// <summary>Invokes one advertised tool through readiness, permission, persistence, and execution checks.</summary>
    /// <param name="toolCall">The requested tool call.</param>
    /// <param name="assistantTurn">The assistant snapshot that issued the call, or <see langword="null"/> when no preamble turn exists.</param>
    /// <param name="cancellationToken">A token that cancels permission and tool execution.</param>
    /// <returns>The persisted outcome. Expected tool failures and security denials are represented in the outcome rather than thrown.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    ValueTask<AgentToolCallOutcome> InvokeToolAsync(
        AgentToolCallRequest toolCall,
        AgentTurnRecord? assistantTurn,
        CancellationToken cancellationToken = default);

    /// <summary>Invokes a batch of advertised tools through the same bounded permission and execution pipeline.</summary>
    /// <param name="toolCalls">The request-ordered calls. The caller must not mutate the collection during invocation.</param>
    /// <param name="assistantTurn">The assistant snapshot that issued the calls, or <see langword="null"/> when no preamble turn exists.</param>
    /// <param name="cancellationToken">A token that cancels pending permission and tool work; already committed outcomes remain durable.</param>
    /// <returns>Persisted outcomes in request order; an empty input produces an empty result in the base host.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    ValueTask<IReadOnlyList<AgentToolCallOutcome>> InvokeToolsAsync(
        IReadOnlyList<AgentToolCallRequest> toolCalls,
        AgentTurnRecord? assistantTurn,
        CancellationToken cancellationToken = default);
}
