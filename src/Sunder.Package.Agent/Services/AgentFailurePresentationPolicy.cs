using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Services;

internal static class AgentFailurePresentationPolicy
{
    internal const string StreamInterruptedMarkdown =
        "### Response stream interrupted\n\nThe response stream was interrupted before it completed. You can retry or continue this session.";
    internal const string StreamInterruptedSummary = "The response stream was interrupted.";
    internal const string GenericFailureMarkdown =
        "### Agent run failed\n\nThe agent run could not be completed. You can retry or continue this session.";
    internal const string GenericFailureSummary = "The agent run could not be completed.";
    internal const string DeadlineInterruptedMarkdown =
        "### Response timed out\n\nThe provider response exceeded its RPC deadline. Recorded tool results are preserved. You can continue this session.";

    private static readonly FailurePresentation StreamInterrupted = new(
        StreamInterruptedMarkdown,
        StreamInterruptedSummary,
        AgentRunStatus.Interrupted,
        AgentLifecycleEventKind.RunInterrupted);

    private static readonly FailurePresentation GenericFailure = new(
        GenericFailureMarkdown,
        GenericFailureSummary,
        AgentRunStatus.Failed,
        AgentLifecycleEventKind.RunFailed);

    internal static FailurePresentation Create(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (IsStreamQueueLimit(exception)) return StreamInterrupted;
        return ContainsRpcFailure(exception, error => error.Kind == SunderRpcErrorKind.DeadlineExceeded
                   && error.Code == "rpc.call.deadline-exceeded")
            ? new(DeadlineInterruptedMarkdown, "The provider response exceeded its RPC deadline.",
                AgentRunStatus.Interrupted, AgentLifecycleEventKind.RunInterrupted)
            : GenericFailure;
    }

    internal static bool IsStreamQueueLimit(Exception exception)
        => ContainsRpcFailure(exception, error => error.Code == AgentChatFailureProjection.StreamQueueLimitErrorCode);

    private static bool ContainsRpcFailure(Exception exception, Func<SunderRpcError, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (current is SunderRpcException rpcException
                && predicate(rpcException.Error))
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                {
                    pending.Push(aggregate.InnerExceptions[index]);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        return false;
    }

    internal sealed record FailurePresentation(
        string Markdown,
        string CheckpointSummary,
        AgentRunStatus Status,
        AgentLifecycleEventKind LifecycleEventKind)
    {
        public bool IsInterrupted => Status == AgentRunStatus.Interrupted;
    }
}
