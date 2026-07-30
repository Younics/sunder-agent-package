using System.ClientModel;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;

namespace Sunder.Package.Agent.Provider.LMStudio;

internal static class LMStudioExceptionMapper
{
    public static AgentChatProviderException Map(Exception exception)
    {
        if (exception is AgentChatProviderException providerException)
        {
            return providerException;
        }

        var detail = exception is ClientResultException { Status: > 0 } resultException
            ? $"LM Studio returned HTTP {resultException.Status}: {exception.Message}"
            : exception.Message;
        return new AgentChatProviderException(
            detail,
            $"### LM Studio request failed\n\n{detail}",
            "lmstudio-sdk-error",
            exception)
        {
            FailureKind = IsContextWindowExceeded(exception)
                ? AgentChatProviderFailureKind.ContextWindowExceeded
                : AgentChatProviderFailureKind.Unknown,
        };
    }

    public static AgentChatProviderException InvalidConfiguration(string detail)
        => new(
            detail,
            $"### LM Studio base URL is invalid\n\n{detail}\n\nOpen **Settings -> Packages -> Sunder Agent Provider LM Studio** and enter a valid URL.",
            "lmstudio-base-url-invalid");

    public static AgentChatProviderException MultipleToolCalls()
        => new(
            "LM Studio requested multiple tool calls.",
            "### LM Studio requested multiple tool calls\n\nEnable multiple tool calls for this request or use a model that emits one call per turn.",
            "lmstudio-multiple-tool-calls");

    public static AgentChatProviderException MalformedToolCall(string detail)
        => new(
            detail,
            $"### LM Studio returned a malformed tool call\n\n{detail}",
            "lmstudio-malformed-tool-call");

    public static AgentChatProviderException IncompleteResponse(string detail)
        => new(
            detail,
            $"### LM Studio response did not complete\n\n{detail}",
            "lmstudio-incomplete-response");

    public static AgentChatProviderException ProviderTimeout(Exception exception)
        => new(
            "The LM Studio request timed out.",
            "### LM Studio request timed out\n\nThe provider canceled the request before the caller requested cancellation.",
            "lmstudio-timeout",
            exception);

    private static bool IsContextWindowExceeded(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (ProviderContextWindowFailureClassifier.IsLmStudio(
                    ProviderErrorDiagnosticExtractor.Extract(current.Message)))
            {
                return true;
            }
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not ClientResultException resultException)
            {
                continue;
            }

            try
            {
                if (ProviderContextWindowFailureClassifier.IsLmStudio(
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
