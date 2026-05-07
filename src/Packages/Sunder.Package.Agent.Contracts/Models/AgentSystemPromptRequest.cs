namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentSystemPromptRequest(
    AgentSessionRecord Session,
    AgentProfileRecord Profile,
    string ProviderId,
    string ModelId,
    AgentProviderRunCapabilities RunCapabilities,
    AgentWorkspaceRecord? Workspace,
    AgentWorkspaceBindingRecord? ExecutionBinding,
    IReadOnlyList<AgentToolDescriptor> AvailableTools,
    IReadOnlyList<AgentTurnRecord> Turns,
    Guid RunId,
    long RunRevision,
    DateTimeOffset RunStartedAtUtc,
    string UserMessage);
