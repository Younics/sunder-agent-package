using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Models;

internal enum AgentRunSuspensionKind
{
    Permission = 0,
    ChildJoin = 1,
}

internal abstract record AgentRunSuspension(AgentRunSuspensionKind Kind);

internal sealed record AgentPermissionRunSuspension(
    string RequestId,
    string ToolCallId,
    Guid UserTurnId)
    : AgentRunSuspension(AgentRunSuspensionKind.Permission);

internal sealed record AgentChildJoinRunSuspension(
    Guid UserTurnId,
    string ToolId,
    string? ArgumentsJson,
    IReadOnlyList<AgentChildJoinTask> OutstandingTasks,
    IReadOnlyList<AgentChildJoinTaskResult> CompletedTasks)
    : AgentRunSuspension(AgentRunSuspensionKind.ChildJoin);

internal sealed record AgentChildJoinTask(
    Guid ChildSessionId,
    string ToolCallId,
    string? Title = null);

internal sealed record AgentChildJoinTaskResult(
    Guid ChildSessionId,
    string ToolCallId,
    AgentRunStatus Status,
    string Summary,
    string? Content,
    string? Title = null);

internal static class AgentRunSuspensionSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(AgentRunSuspension suspension)
        => suspension switch
        {
            AgentPermissionRunSuspension permission => JsonSerializer.Serialize(permission, JsonOptions),
            AgentChildJoinRunSuspension childJoin => JsonSerializer.Serialize(childJoin, JsonOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(suspension), suspension, "Unknown run suspension type."),
        };

    public static AgentRunSuspension Deserialize(AgentRunSuspensionKind kind, string dataJson)
        => kind switch
        {
            AgentRunSuspensionKind.Permission => JsonSerializer.Deserialize<AgentPermissionRunSuspension>(dataJson, JsonOptions)
                ?? throw new InvalidOperationException("Permission suspension data was empty."),
            AgentRunSuspensionKind.ChildJoin => JsonSerializer.Deserialize<AgentChildJoinRunSuspension>(dataJson, JsonOptions)
                ?? throw new InvalidOperationException("Child-join suspension data was empty."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown run suspension kind."),
        };
}

internal sealed record AgentRunSuspensionResult(
    string ContinuationToken,
    AgentRunCheckpointRecord Checkpoint);

internal sealed record AgentChildJoinTransitionResult(
    AgentChildJoinTransitionOutcome Outcome,
    AgentChildJoinRunSuspension? Suspension = null,
    AgentRunCheckpointRecord? Checkpoint = null,
    AgentParentContinuationWorkRecord? Work = null,
    long? Epoch = null)
{
    public bool IsAccepted => Outcome is AgentChildJoinTransitionOutcome.Waiting or AgentChildJoinTransitionOutcome.Ready;

    public bool IsReady => Outcome == AgentChildJoinTransitionOutcome.Ready;
}

internal enum AgentChildJoinTransitionOutcome
{
    Rejected = 0,
    Waiting = 1,
    Ready = 2,
}
