using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Services;

internal static class AgentChatFailureProjection
{
    internal const string StreamQueueLimitErrorCode = "rpc.worker.stream-queue-limit";
    internal const string UnsafeRpcFailureErrorCode = "rpc.worker.unsafe-dispatch";

    internal static AgentChatErrorEvent CreateErrorEvent(AgentChatProviderException exception)
    {
        var normalized = AgentProviderFailureClassifier.Normalize(exception);
        var code = normalized.ErrorCode ?? "provider-error";
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (current is SunderRpcException rpc)
            {
                if (rpc.Error.Code == StreamQueueLimitErrorCode) { code = StreamQueueLimitErrorCode; break; }
                if (rpc.Error.DispatchState != SunderRpcDispatchState.NotDispatched
                    || rpc.Error.RetrySafety != SunderRpcRetrySafety.Safe
                    || rpc.Error.Kind is not (SunderRpcErrorKind.Unavailable or SunderRpcErrorKind.DeadlineExceeded))
                    code = UnsafeRpcFailureErrorCode;
            }
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
        return new(code, normalized.Message, normalized.Content, normalized.FailureKind,
            normalized.StatusCode, normalized.RetryAfterMilliseconds);
    }

    internal static SunderRpcException? CreateProjectedRpcException(AgentChatErrorEvent error)
    {
        var kind = error.ErrorCode switch
        {
            StreamQueueLimitErrorCode => SunderRpcErrorKind.ResourceExhausted,
            UnsafeRpcFailureErrorCode => SunderRpcErrorKind.Unavailable,
            _ => (SunderRpcErrorKind?)null,
        };
        return kind is null
            ? null
            : new SunderRpcException(new SunderRpcError(
                kind.Value,
                error.ErrorCode,
                error.ErrorMessage,
                SunderRpcDispatchState.PossiblyDispatched,
                SunderRpcRetrySafety.Unsafe));
    }

    internal static AgentChatProviderException CreateProviderException(AgentChatErrorEvent error)
        => new(
            error.ErrorMessage,
            error.ErrorContent,
            error.ErrorCode,
            CreateProjectedRpcException(error))
        {
            FailureKind = error.ErrorFailureKind,
            StatusCode = error.StatusCode,
            RetryAfterMilliseconds = error.RetryAfterMilliseconds,
        };
}
