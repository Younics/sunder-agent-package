using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Provider.Gemini;

internal sealed class GeminiTelemetry(AgentChatClientContext context)
{
    public async ValueTask RequestStartedAsync(
        string modelId,
        int messageCount,
        int toolCount,
        int systemPromptLength,
        CancellationToken cancellationToken)
    {
        await WriteAsync(
            AgentLogLevel.Debug,
            "provider.request.start",
            "Provider request started.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model.id"] = modelId,
                ["prompt.turn_count"] = messageCount,
                ["tool.available_count"] = toolCount,
                ["system_prompt.length"] = systemPromptLength,
            },
            cancellationToken: cancellationToken);
        await WriteAsync(
            AgentLogLevel.Debug,
            "provider.stream.start",
            "Provider stream started.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["provider.id"] = context.ProviderId,
                ["model.id"] = modelId,
                ["tool.count"] = toolCount,
                ["message.count"] = messageCount,
                ["system_prompt.length"] = systemPromptLength,
            },
            cancellationToken: cancellationToken);
    }

    public ValueTask FirstEventAsync(string eventKind, long elapsedMilliseconds, CancellationToken cancellationToken)
        => WriteAsync(
            AgentLogLevel.Debug,
            "provider.stream.first_event",
            eventKind,
            elapsedMilliseconds,
            cancellationToken: cancellationToken);

    public ValueTask CompletedAsync(bool hadEvents, long elapsedMilliseconds, CancellationToken cancellationToken)
        => WriteAsync(
            AgentLogLevel.Debug,
            "provider.stream.completed",
            hadEvents ? null : "Provider stream ended without events.",
            elapsedMilliseconds,
            cancellationToken: cancellationToken);

    public ValueTask CanceledAsync(long elapsedMilliseconds)
        => WriteAsync(
            AgentLogLevel.Warning,
            "provider.stream.canceled",
            "Provider stream was canceled.",
            elapsedMilliseconds,
            cancellationToken: CancellationToken.None);

    public ValueTask FailedAsync(Exception exception, long elapsedMilliseconds)
        => WriteAsync(
            AgentLogLevel.Error,
            "provider.stream.failed",
            exception.Message,
            elapsedMilliseconds,
            exception: exception,
            cancellationToken: CancellationToken.None);

    public ValueTask UnsupportedResponseAsync(
        IReadOnlyList<string> partKinds,
        long elapsedMilliseconds,
        CancellationToken cancellationToken)
        => WriteAsync(
            AgentLogLevel.Warning,
            "provider.response.unsupported_content",
            $"Gemini returned unsupported response content: {string.Join(", ", partKinds)}.",
            elapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["content.kinds"] = string.Join(",", partKinds),
                ["content.count"] = partKinds.Count,
            },
            cancellationToken: cancellationToken);

    private ValueTask WriteAsync(
        AgentLogLevel level,
        string eventName,
        string? message = null,
        long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
        => context.LogProviderEventAsync(
            level,
            eventName,
            message ?? eventName,
            elapsedMilliseconds,
            attributes,
            exception,
            cancellationToken);
}
