using System.Collections.Concurrent;
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using ISessionCleanerService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Session_Cleaner.ISessionCleanerService;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Provider.OpenAI.Transport;

[SunderServiceProvider(typeof(ISessionCleanerService), ProviderId = "openai.continuation.cleaner")]
public sealed partial class CodexResponseContinuationStore : ISessionCleanerService
{
    private const string KeySeparator = ":";
    private readonly ConcurrentDictionary<string, CodexResponseContinuationState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _responsesLiteSessionIds = new(StringComparer.Ordinal);

    public string CleanerId { get; } = "younics.ai.agent.provider.openai:codex-response-continuation";

    public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentServiceIdentity { Id = CleanerId, DisplayName = CleanerId });
    }

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

    public ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = sessionId.ToString("N") + KeySeparator;
        foreach (var key in _states.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            _states.TryRemove(key, out _);
        }

        foreach (var key in _responsesLiteSessionIds.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            _responsesLiteSessionIds.TryRemove(key, out _);
        }
        return ValueTask.CompletedTask;
    }

    private static string BuildKey(string conversationId) => conversationId + KeySeparator;
}

internal sealed record CodexResponseContinuationState(
    string ConversationId,
    string ShapeFingerprint,
    string ResponseId,
    IReadOnlyList<string> ConversationItemFingerprints);
