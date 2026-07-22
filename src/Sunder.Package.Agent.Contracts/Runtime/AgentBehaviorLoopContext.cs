namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Supplies the immutable host-selected configuration and run identity for one behavior-loop invocation.
/// </summary>
/// <remarks>
/// This is a point-in-time execution snapshot, not a live catalog. Run identity comprises both
/// <paramref name="RunId"/> and <paramref name="RunRevision"/>. The revision is a monotonically increasing generation
/// within the session and remains constant while the same durable run advances through host-internal lease epochs.
/// Stale loop work must stop when <see cref="Contracts.IAgentBehaviorLoopRuntime.IsCurrentRun"/> returns false.
/// </remarks>
/// <param name="Session">The persisted session snapshot being processed.</param>
/// <param name="Profile">The profile snapshot resolved before execution.</param>
/// <param name="ProviderId">The selected chat provider identifier.</param>
/// <param name="ModelId">The selected provider-specific model identifier.</param>
/// <param name="RunCapabilities">The selected provider/model capabilities used to shape the run.</param>
/// <param name="Workspace">The assigned workspace snapshot, or <see langword="null"/> when unavailable.</param>
/// <param name="ExecutionBinding">The selected primary execution-target binding, or <see langword="null"/> for host-only execution.</param>
/// <param name="RunId">The globally unique identity of this durable run.</param>
/// <param name="RunRevision">The per-session run generation used to fence older runs and correlate checkpoints.</param>
/// <param name="RunningCheckpoint">The durable running checkpoint created before behavior-loop execution.</param>
/// <param name="RunStartedAtUtc">The UTC time at which this run generation was reserved.</param>
/// <param name="UserMessage">The user message that started this run.</param>
/// <param name="UserTurnId">The stable identifier of the persisted user turn for this run.</param>
/// <param name="ModelVariant">An optional provider-defined model variant selected for this run.</param>
/// <param name="ModelSpeedOption">An optional provider-defined speed option selected for this run.</param>
/// <param name="ModelModeOption">An optional provider-defined operating mode selected for this run.</param>
public sealed record AgentBehaviorLoopContext(
    AgentSessionRecord Session,
    AgentProfileRecord Profile,
    string ProviderId,
    string ModelId,
    AgentProviderRunCapabilities RunCapabilities,
    AgentWorkspaceRecord? Workspace,
    AgentWorkspaceBindingRecord? ExecutionBinding,
    Guid RunId,
    long RunRevision,
    AgentRunCheckpointRecord RunningCheckpoint,
    DateTimeOffset RunStartedAtUtc,
    string UserMessage,
    Guid UserTurnId,
    AgentModelVariantDescriptor? ModelVariant = null,
    AgentModelSpeedOptionDescriptor? ModelSpeedOption = null,
    AgentModelModeOptionDescriptor? ModelModeOption = null);
