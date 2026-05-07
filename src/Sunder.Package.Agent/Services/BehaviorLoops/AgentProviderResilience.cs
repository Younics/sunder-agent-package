using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Polly;
using Polly.Retry;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal static class AgentProviderResilience
{
    public const int MaxTransientRetryAttempts = 3;

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(7),
    ];

    public static ResiliencePipeline CreatePipeline(Action<AgentProviderRetryNotification> onRetry)
        => new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = MaxTransientRetryAttempts,
                DelayGenerator = args => ValueTask.FromResult<TimeSpan?>(GetDelay(args.AttemptNumber)),
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is { } exception
                    && IsTransient(exception, args.Context.CancellationToken)),
                OnRetry = args
                    =>
                    {
                        if (args.Outcome.Exception is { } exception)
                        {
                            onRetry(new AgentProviderRetryNotification(
                                args.AttemptNumber + 1,
                                MaxTransientRetryAttempts,
                                GetDelay(args.AttemptNumber),
                                exception));
                        }

                        return ValueTask.CompletedTask;
                    },
            })
            .Build();

    public static bool IsTransient(Exception exception, CancellationToken cancellationToken = default)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (exception is AgentChatProviderException providerException)
        {
            if (IsNonRetryableProviderErrorCode(providerException.ErrorCode))
            {
                return false;
            }

            if (providerException.InnerException is not null)
            {
                return IsTransient(providerException.InnerException, cancellationToken)
                       || IsTransientMessage(providerException.Message)
                       || IsTransientMessage(providerException.Content);
            }
        }

        if (exception is HttpRequestException httpRequestException)
        {
            return IsRetryableStatusCode(httpRequestException.StatusCode)
                   || !httpRequestException.StatusCode.HasValue
                   || IsTransientMessage(httpRequestException.Message);
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
                || (exception is AgentChatProviderException chatProviderException && IsTransientMessage(chatProviderException.Content))
                || (exception.InnerException is not null && IsTransient(exception.InnerException, cancellationToken));
    }

    private static TimeSpan GetDelay(int attemptNumber)
        => attemptNumber >= 0 && attemptNumber < RetryDelays.Length
            ? RetryDelays[attemptNumber]
            : RetryDelays[^1];

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

internal sealed record AgentProviderRetryNotification(
    int AttemptNumber,
    int MaxRetryAttempts,
    TimeSpan Delay,
    Exception Exception);
