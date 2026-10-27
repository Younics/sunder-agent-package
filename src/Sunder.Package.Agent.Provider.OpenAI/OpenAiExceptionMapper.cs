using System.ClientModel;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal static class OpenAiExceptionMapper
{
    public static AgentChatProviderException MapRequest(Exception exception)
    {
        if (TryMapContextWindowExceeded(exception, out var overflow)) return overflow;
        if (exception is AgentChatProviderException existing) return AgentProviderFailureClassifier.Normalize(existing);
        var status = exception is ClientResultException { Status: > 0 } result ? (int?)result.Status
            : exception is HttpRequestException http && http.StatusCode is { } code ? (int)code : null;
        string? milliseconds = null, retryAfter = null;
        if (exception is ClientResultException sdk && sdk.GetRawResponse() is { } response)
        {
            response.Headers.TryGetValue("retry-after-ms", out milliseconds);
            response.Headers.TryGetValue("retry-after", out retryAfter);
        }
        return new(exception.Message, $"### OpenAI request failed\n\n{exception.Message}", "openai-request-error", exception)
        {
            StatusCode = status,
            FailureKind = exception is OperationCanceledException ? AgentChatProviderFailureKind.Timeout
                : AgentProviderFailureClassifier.Classify(ProviderErrorDiagnosticExtractor.Extract(exception.Message), status),
            RetryAfterMilliseconds = AgentProviderFailureClassifier.RetryAfter(milliseconds, retryAfter, DateTimeOffset.UtcNow),
        };
    }

    public static bool TryMapContextWindowExceeded(
        Exception exception,
        out AgentChatProviderException providerException)
    {
        if (exception is AgentChatProviderException
            {
                FailureKind: AgentChatProviderFailureKind.ContextWindowExceeded,
            } classifiedException)
        {
            providerException = classifiedException;
            return true;
        }

        if (!IsContextWindowExceeded(exception))
        {
            providerException = null!;
            return false;
        }

        providerException = new AgentChatProviderException(
            "The OpenAI request exceeded the model context window.",
            "### OpenAI context window exceeded\n\nThe request exceeded the selected model's context window.",
            "openai-context-window-exceeded",
            exception)
        {
            FailureKind = AgentChatProviderFailureKind.ContextWindowExceeded,
        };
        return true;
    }

    private static bool IsContextWindowExceeded(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (ProviderContextWindowFailureClassifier.IsOpenAi(
                    ProviderErrorDiagnosticExtractor.Extract(current.Message)))
            {
                return true;
            }

            if (current is not ClientResultException resultException)
            {
                continue;
            }

            try
            {
                if (ProviderContextWindowFailureClassifier.IsOpenAi(
                        ProviderErrorDiagnosticExtractor.Extract(
                            resultException.GetRawResponse()?.Content.ToString())))
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // Streaming responses are not guaranteed to have buffered error content.
            }
        }

        return false;
    }
}
