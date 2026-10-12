using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Services;

internal static class AgentChatFailureProjection
{
    internal const string StreamQueueLimitErrorCode = "rpc.worker.stream-queue-limit";
    internal const string UnsafeRpcFailureErrorCode = "rpc.worker.unsafe-dispatch";

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
        };
}
