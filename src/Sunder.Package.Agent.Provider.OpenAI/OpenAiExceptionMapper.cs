using System.ClientModel;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.Shared;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal static class OpenAiExceptionMapper
{
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
                    OpenAiErrorDiagnosticExtractor.Extract(current.Message)))
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
                        OpenAiErrorDiagnosticExtractor.Extract(
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
