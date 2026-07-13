using System.Collections.Concurrent;
using Sunder.Package.Agent.Contracts.Contracts;

namespace Sunder.Package.Agent.Provider.OpenAI.Transport;

public sealed class CodexResponseContinuationStore : IAgentSessionDataCleaner
{
    private const string KeySeparator = ":";
    private readonly ConcurrentDictionary<string, CodexResponseContinuationState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _responsesLiteSessionIds = new(StringComparer.Ordinal);

    public string CleanerId { get; } = "sunder.package.agent.provider.openai:codex-response-continuation";

    internal CodexResponseContinuationState? Get(string? conversationId)
        => string.IsNullOrWhiteSpace(conversationId)
            ? null
            : _states.TryGetValue(BuildKey(conversationId), out var state)
                ? state
                : null;

    internal void Save(CodexResponseContinuationState state)
    {
        if (string.IsNullOrWhiteSpace(state.ConversationId) || string.IsNullOrWhiteSpace(state.ResponseId))
        {
            return;
        }

        _states[BuildKey(state.ConversationId)] = state;
    }

    internal void Clear(string? conversationId)
    {
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            _states.TryRemove(BuildKey(conversationId), out _);
        }
    }

    internal string GetOrCreateResponsesLiteSessionId(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        return _responsesLiteSessionIds.GetOrAdd(
            BuildKey(conversationId),
            static _ => Guid.CreateVersion7().ToString("D"));
    }

    public void DeleteSessionData(Guid sessionId)
    {
        var prefix = sessionId.ToString("N") + KeySeparator;
        foreach (var key in _states.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            _states.TryRemove(key, out _);
        }

        foreach (var key in _responsesLiteSessionIds.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            _responsesLiteSessionIds.TryRemove(key, out _);
        }
    }

    private static string BuildKey(string conversationId) => conversationId + KeySeparator;
}

internal sealed record CodexResponseContinuationState(
    string ConversationId,
    string ShapeFingerprint,
    string ResponseId,
    IReadOnlyList<string> ConversationItemFingerprints);
