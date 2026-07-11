using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Provider.Anthropic;

internal static class AnthropicExceptionMapper
{
    internal static AgentChatProviderException MissingApiKey()
        => new(
            "missing-api-key",
            "### Missing Anthropic API key\n\nOpen **Settings -> Packages -> Sunder Agent Provider Anthropic** and enter an API key before sending messages.",
            "missing-api-key");

    internal static AgentChatProviderException MultipleToolCalls()
        => new(
            "anthropic-multiple-tool-calls",
            "### Anthropic requested multiple tool calls\n\nSunder currently supports one tool call per assistant turn.",
            "anthropic-multiple-tool-calls");

    internal static AgentChatProviderException MalformedToolCall(string detail)
        => new(
            detail,
            $"### Anthropic returned a malformed tool call\n\n{detail}",
            "anthropic-malformed-tool-call");

    internal static AgentChatProviderException IncompleteResponse(string detail)
        => new(
            detail,
            $"### Anthropic response did not complete\n\n{detail}",
            "anthropic-incomplete-response");

    internal static AgentChatProviderException UnsupportedToolMode(ChatToolMode? toolMode)
        => new(
            $"Anthropic does not support tool mode '{toolMode?.GetType().Name ?? "null"}'.",
            $"### Unsupported Anthropic tool mode\n\n`{toolMode?.GetType().Name ?? "null"}` cannot be mapped to Anthropic tool choice.",
            "anthropic-unsupported-tool-mode");

    internal static AgentChatProviderException UnsupportedThinkingToolChoice()
        => new(
            "Anthropic extended thinking supports only automatic tool choice.",
            "### Unsupported Anthropic reasoning and tool mode\n\nExtended thinking cannot be combined with required or specific tool choice. Use automatic tool choice or disable visible reasoning.",
            "anthropic-unsupported-reasoning-tool-choice");

    internal static AgentChatProviderException ProviderTimeout(OperationCanceledException exception)
        => new(
            "The Anthropic request timed out.",
            "### Anthropic request timed out\n\nThe provider canceled the request before the caller requested cancellation.",
            "anthropic-timeout",
            exception);

    internal static AgentChatProviderException Map(Exception exception)
        => new(
            exception.Message,
            $"### Anthropic request failed\n\n{exception.Message}",
            "anthropic-http-error",
            exception);
}
