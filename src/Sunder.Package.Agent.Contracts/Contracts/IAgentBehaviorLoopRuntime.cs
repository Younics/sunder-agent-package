using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentBehaviorLoopRuntime
{
    bool IsCurrentRun();

    IReadOnlyList<AgentTurnRecord> ListTurns();

    ValueTask<AgentBehaviorInstructionContext> BuildInstructionContextAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AgentRuntimeTool>> ListReadyToolsAsync(CancellationToken cancellationToken = default);

    ValueTask<IChatClient> CreateChatClientAsync(
        AgentChatClientContext context,
        CancellationToken cancellationToken = default);

    AgentRunCheckpointRecord SaveCheckpoint(AgentRunStatus status, string? summary);

    void LogEvent(
        AgentLogLevel level,
        string eventName,
        string message,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null);

    AgentTurnRecord UpsertAssistantTurn(AgentTurnRecord? assistantTurn, string content);

    ValueTask PublishLifecycleEventAsync(
        AgentLifecycleEventKind kind,
        AgentRunStatus status,
        AgentTurnRecord? triggerTurn = null,
        AgentRunCheckpointRecord? checkpoint = null,
        bool isInterrupted = false,
        CancellationToken cancellationToken = default);

    ValueTask<AgentToolCallOutcome> InvokeToolAsync(
        AgentToolCallRequest toolCall,
        AgentTurnRecord? assistantTurn,
        CancellationToken cancellationToken = default);
}
