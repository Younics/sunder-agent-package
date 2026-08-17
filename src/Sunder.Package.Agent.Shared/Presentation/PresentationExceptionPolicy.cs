using System.Net.Http;
using System.Runtime.InteropServices;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Shared.Presentation;

internal static class PresentationExceptionPolicy
{
    public static bool IsFatal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or SEHException)
        {
            return true;
        }

        if (exception is AggregateException aggregate
            && aggregate.InnerExceptions.Any(IsFatal))
        {
            return true;
        }

        return exception.InnerException is { } inner && IsFatal(inner);
    }

    public static bool IsExpectedOperationFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (IsFatal(exception))
        {
            return false;
        }

        if (exception is OperationCanceledException
            or ObjectDisposedException
            or PresentationOperationException
            or TimeoutException
            or HttpRequestException
            or UnauthorizedAccessException
            or IOException)
        {
            return true;
        }

        if (exception is SunderRpcException rpcException)
        {
            return rpcException.Error.Kind is SunderRpcErrorKind.Domain
                or SunderRpcErrorKind.Validation
                or SunderRpcErrorKind.Cancelled
                or SunderRpcErrorKind.Unavailable
                or SunderRpcErrorKind.StaleEndpoint
                or SunderRpcErrorKind.DeadlineExceeded;
        }

        return exception is AggregateException aggregate
               && aggregate.InnerExceptions.Count > 0
               && aggregate.InnerExceptions.All(IsExpectedOperationFailure);
    }
}

internal sealed class PresentationOperationException : InvalidOperationException
{
    public PresentationOperationException(string message)
        : base(message)
    {
    }

    public PresentationOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
