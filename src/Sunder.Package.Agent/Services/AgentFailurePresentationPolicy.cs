using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Services;

internal static class AgentFailurePresentationPolicy
{
    internal const string StreamQueueLimitErrorCode = "rpc.worker.stream-queue-limit";
    internal const string StreamInterruptedMarkdown =
        "### Response stream interrupted\n\nThe response stream was interrupted before it completed. You can retry or continue this session.";
    internal const string StreamInterruptedSummary = "The response stream was interrupted.";
    internal const string GenericFailureMarkdown =
        "### Agent run failed\n\nThe agent run could not be completed. You can retry or continue this session.";
    internal const string GenericFailureSummary = "The agent run could not be completed.";

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
        return IsStreamQueueLimit(exception) ? StreamInterrupted : GenericFailure;
    }

    internal static bool IsStreamQueueLimit(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (current is SunderRpcException rpcException
                && string.Equals(
                    rpcException.Error.Code,
                    StreamQueueLimitErrorCode,
                    StringComparison.Ordinal))
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
