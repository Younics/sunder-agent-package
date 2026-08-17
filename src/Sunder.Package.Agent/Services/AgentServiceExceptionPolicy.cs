using System.Runtime.InteropServices;

namespace Sunder.Package.Agent.Services;

internal static class AgentServiceExceptionPolicy
{
    internal static bool IsFatal(Exception exception)
    {
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
}
