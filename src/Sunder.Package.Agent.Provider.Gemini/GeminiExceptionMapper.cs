using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Provider.Gemini;

internal static class GeminiExceptionMapper
{
    public static AgentChatProviderException MissingApiKey()
        => new(
            "missing-api-key",
            "### Missing Gemini API key\n\nOpen **Settings -> Packages -> Sunder Agent Provider Gemini** and enter an API key before sending messages.",
            "missing-api-key");

    public static AgentChatProviderException ClientInitialization(Exception exception)
        => new(
            exception.Message,
            $"### Gemini client setup failed\n\n{exception.Message}",
            "gemini-client-init",
            exception);

    public static AgentChatProviderException Request(Exception exception)
        => exception as AgentChatProviderException ?? new AgentChatProviderException(
            exception.Message,
            $"### Gemini request failed\n\n{exception.Message}",
            "gemini-http-error",
            exception);

    public static AgentChatProviderException MultipleToolCalls()
        => new(
            "gemini-multiple-tool-calls",
            "### Gemini requested multiple tool calls\n\nSunder currently supports one tool call per assistant turn.",
            "gemini-multiple-tool-calls");

    public static AgentChatProviderException UnsupportedContent(AIContent content, ChatRole role)
        => new(
            $"Gemini does not support {content.GetType().Name} content in the '{role}' role.",
            $"### Unsupported Gemini content\n\n`{content.GetType().Name}` content is not supported in the `{role}` role.",
            "gemini-unsupported-content");

    public static AgentChatProviderException UnsupportedRole(ChatRole role)
        => new(
            $"Gemini does not support the '{role}' chat role.",
            $"### Unsupported Gemini role\n\nThe `{role}` chat role cannot be sent to Gemini.",
            "gemini-unsupported-role");

    public static AgentChatProviderException UnsupportedTool(AITool tool)
        => new(
            $"Gemini does not support the {tool.GetType().Name} tool type.",
            $"### Unsupported Gemini tool\n\n`{tool.GetType().Name}` cannot be sent as a Gemini function declaration.",
            "gemini-unsupported-tool");

    public static AgentChatProviderException InvalidThoughtSignature(Exception exception)
        => new(
            "Gemini thought signature was not valid base64.",
            "### Invalid Gemini thought signature\n\nThe saved Gemini reasoning signature is malformed.",
            "gemini-invalid-thought-signature",
            exception);

    public static AgentChatProviderException MalformedToolCall(string detail)
        => new(
            detail,
            $"### Gemini returned a malformed tool call\n\n{detail}",
            "gemini-malformed-tool-call");

    public static AgentChatProviderException IncompleteResponse(string detail)
        => new(
            detail,
            $"### Gemini response did not complete\n\n{detail}",
            "gemini-incomplete-response");

    public static AgentChatProviderException UnsupportedToolMode(ChatToolMode? toolMode)
        => new(
            $"Gemini does not support tool mode '{toolMode?.GetType().Name ?? "null"}'.",
            $"### Unsupported Gemini tool mode\n\n`{toolMode?.GetType().Name ?? "null"}` cannot be mapped to Gemini function calling.",
            "gemini-unsupported-tool-mode");

    public static AgentChatProviderException UnsupportedReasoning(string modelId, ReasoningEffort? effort)
        => new(
            $"Gemini model '{modelId}' does not support reasoning effort '{effort}'.",
            $"### Unsupported Gemini reasoning option\n\n`{modelId}` does not expose the requested reasoning control.",
            "gemini-unsupported-reasoning");

    public static AgentChatProviderException UnsupportedResponse(IReadOnlyList<string> partKinds)
        => new(
            $"Gemini returned unsupported response parts: {string.Join(", ", partKinds)}.",
            $"### Unsupported Gemini response\n\nThe response contained unsupported parts: `{string.Join("`, `", partKinds)}`.",
            "gemini-unsupported-response");

    public static AgentChatProviderException ProviderTimeout(OperationCanceledException exception)
        => new(
            "The Gemini request timed out.",
            "### Gemini request timed out\n\nThe provider canceled the request before the caller requested cancellation.",
            "gemini-timeout",
            exception);
}
