using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal static class AgentProviderResilience
{
    public const int MaxTransientRetryAttempts = 10;

    public static bool IsTransient(Exception exception, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        if (AgentFailurePresentationPolicy.IsStreamQueueLimit(exception))
        {
            return false;
        }
        if (HasTerminalProviderFailure(exception)) return false;

        // Structured dispatch metadata is an unconditional safety boundary, even
        // when an outer provider wrapper says overload/timeout.
        if (TryClassifyRpcFailure(exception, out var retryable)) return retryable;

        if (exception is AgentChatProviderException rawProviderException)
        {
            var providerException = AgentProviderFailureClassifier.Normalize(rawProviderException);
            if (providerException.FailureKind == AgentChatProviderFailureKind.ContextWindowExceeded)
            {
                return false;
            }

            if (IsNonRetryableProviderErrorCode(providerException.ErrorCode))
            {
                return false;
            }
            return providerException.FailureKind switch
            {
                AgentChatProviderFailureKind.Transient or AgentChatProviderFailureKind.Overloaded
                    or AgentChatProviderFailureKind.RateLimited or AgentChatProviderFailureKind.Timeout => true,
                AgentChatProviderFailureKind.Unknown => providerException.InnerException is { } inner
                    && IsTransient(inner, cancellationToken),
                _ => false,
            };
        }

        if (exception is HttpRequestException httpRequestException)
        {
            return !httpRequestException.StatusCode.HasValue
                   || IsRetryableStatusCode(httpRequestException.StatusCode);
        }

        if (exception is IOException or SocketException or TimeoutException)
        {
            return true;
        }

        if (exception is TaskCanceledException or OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        return IsTransientByTypeName(exception)
                || IsTransientMessage(exception.Message)
                || (exception.InnerException is not null && IsTransient(exception.InnerException, cancellationToken));
    }

    private static bool TryClassifyRpcFailure(Exception exception, out bool retryable)
    {
        var found = false;
        retryable = false;
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (current is SunderRpcException rpcException)
            {
                found = true;
                if (rpcException.Error.Kind is not (SunderRpcErrorKind.DeadlineExceeded or SunderRpcErrorKind.Unavailable)
                    || rpcException.Error.DispatchState != SunderRpcDispatchState.NotDispatched
                    || rpcException.Error.RetrySafety != SunderRpcRetrySafety.Safe)
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

        retryable = found;
        return found;
    }

    private static bool HasTerminalProviderFailure(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (current is AgentChatProviderException provider)
            {
                var kind = AgentProviderFailureClassifier.Normalize(provider).FailureKind;
                if (kind is AgentChatProviderFailureKind.Authentication or AgentChatProviderFailureKind.QuotaExceeded
                    or AgentChatProviderFailureKind.InvalidRequest or AgentChatProviderFailureKind.ContentPolicy
                    or AgentChatProviderFailureKind.ContextWindowExceeded || IsNonRetryableProviderErrorCode(provider.ErrorCode))
                    return true;
            }
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
        return false;
    }

    private static bool IsRetryableStatusCode(HttpStatusCode? statusCode)
    {
        if (!statusCode.HasValue)
        {
            return false;
        }

        var code = (int)statusCode.Value;
        return statusCode.Value == HttpStatusCode.RequestTimeout
               || statusCode.Value == HttpStatusCode.TooManyRequests
               || code == 425
               || code == 529
               || code >= 500;
    }

    private static bool IsNonRetryableProviderErrorCode(string? errorCode)
        => !string.IsNullOrWhiteSpace(errorCode)
           && (errorCode.Contains("missing-api-key", StringComparison.OrdinalIgnoreCase)
               || errorCode.Contains("auth-required", StringComparison.OrdinalIgnoreCase)
                || errorCode.Contains("multiple-tool-calls", StringComparison.OrdinalIgnoreCase)
                || errorCode.Contains("client-init", StringComparison.OrdinalIgnoreCase)
                || errorCode.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
                || errorCode.Contains("malformed", StringComparison.OrdinalIgnoreCase)
               || errorCode.Contains("invalid", StringComparison.OrdinalIgnoreCase));

    private static bool IsTransientByTypeName(Exception exception)
    {
        var typeName = exception.GetType().Name;
        return typeName.Contains("HttpIOException", StringComparison.OrdinalIgnoreCase)
               || typeName.Contains("SocketException", StringComparison.OrdinalIgnoreCase)
               || typeName.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
               || typeName.Contains("Connection", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransientMessage(string? message)
        => !string.IsNullOrWhiteSpace(message)
           && (message.Contains("ResponseEnded", StringComparison.OrdinalIgnoreCase)
               || message.Contains("response ended prematurely", StringComparison.OrdinalIgnoreCase)
               || message.Contains("connection", StringComparison.OrdinalIgnoreCase)
               || message.Contains("temporarily", StringComparison.OrdinalIgnoreCase)
               || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
               || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
               || message.Contains("socket", StringComparison.OrdinalIgnoreCase)
               || message.Contains("transport", StringComparison.OrdinalIgnoreCase)
               || message.Contains("503", StringComparison.OrdinalIgnoreCase)
               || message.Contains("502", StringComparison.OrdinalIgnoreCase)
               || message.Contains("529", StringComparison.OrdinalIgnoreCase)
                || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase));
}

// One instance per logical step, shared across partial-response continuations.
// Merely receiving output must never replenish an exhausted recovery allowance.
internal sealed class AgentProviderRetryState(TimeProvider? timeProvider = null, Func<double>? random = null)
{
    internal TimeProvider Clock { get; } = timeProvider ?? TimeProvider.System;
    private int _retries;
    private int _timeouts;
    internal int RetryCount => _retries;

    internal AgentRunRetryState? TrySchedule(Exception error, bool continuation, CancellationToken token)
    {
        if (!AgentProviderResilience.IsTransient(error, token) || _retries >= AgentProviderResilience.MaxTransientRetryAttempts) return null;
        var provider = error is AgentChatProviderException value ? AgentProviderFailureClassifier.Normalize(value) : null;
        var kind = provider?.FailureKind ?? AgentProviderFailureClassifier.Classify(error.Message);
        if ((kind == AgentChatProviderFailureKind.Timeout || error is TimeoutException or OperationCanceledException) && ++_timeouts > 3) return null;
        var jitter = 0.8 + 0.4 * Math.Clamp((random ?? Random.Shared.NextDouble)(), 0, 1);
        var baseMs = Math.Min(2_000L << Math.Min(_retries, 3), 10_000L);
        var delayMs = Math.Max(Math.Round(baseMs * jitter), Math.Clamp(provider?.RetryAfterMilliseconds ?? 0, 0, 900_000));
        var reason = kind switch
        {
            AgentChatProviderFailureKind.Overloaded => "Servers overloaded",
            AgentChatProviderFailureKind.RateLimited => "Provider rate limit reached",
            AgentChatProviderFailureKind.Timeout => "Provider request timed out",
            _ => "Provider connection interrupted",
        };
        return new(++_retries + 1, Clock.GetUtcNow().AddMilliseconds(delayMs), reason, continuation);
    }
}

internal interface IAgentProviderRetrySink
{
    void ReportProviderRetry(AgentRunRetryState retry);
}
