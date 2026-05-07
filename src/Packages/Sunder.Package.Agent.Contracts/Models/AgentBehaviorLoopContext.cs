namespace Sunder.Package.Agent.Contracts.Models;

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
    AgentModelVariantDescriptor? ModelVariant = null);
