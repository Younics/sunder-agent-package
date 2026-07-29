using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services;

internal sealed record AgentRunPlan(
    AgentDurableRunKey RunKey,
    AgentActiveRunHandle RunHandle,
    DateTimeOffset StartedAtUtc,
    AgentSessionRecord Session,
    AgentProfileRecord Profile,
    AgentWorkspaceRecord Workspace,
    AgentRunProviderSelection ProviderSelection,
    AgentProfileModelBindingRecord ChatBinding,
    AgentProviderRunCapabilities RunCapabilities,
    AgentModelVariantDescriptor? ModelVariant,
    AgentModelSpeedOptionDescriptor? ModelSpeedOption,
    AgentModelModeOptionDescriptor? ModelModeOption,
    IReadOnlyList<AgentStoredAttachment> Attachments,
    string UserMessage,
    Guid? RollbackAnchorTurnId,
    bool ShouldGenerateSessionTitle) : IDisposable
{
    internal IAgentChatProvider Provider => ProviderSelection.Provider;

    public void Dispose() => ProviderSelection.Dispose();
}

internal abstract record AgentRunPreparationResult;

internal sealed record AgentRunPrepared(AgentRunPlan Plan) : AgentRunPreparationResult;

internal sealed record AgentRunPreparationFailed(string Summary) : AgentRunPreparationResult;

internal abstract record AgentRunStartResult;

internal sealed record AgentRunStarted(
    AgentRunPlan Plan,
    AgentTurnRecord UserTurn,
    AgentRunCheckpointRecord RunningCheckpoint,
    CancellationToken RunCancellationToken,
    AgentRunCheckpointRecord? InterruptedCheckpoint) : AgentRunStartResult;

internal sealed record AgentRunStartInterrupted(AgentRunCheckpointRecord Checkpoint)
    : AgentRunStartResult;
