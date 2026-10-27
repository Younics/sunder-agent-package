using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentSessionService
{
    private readonly object _activitySync = new();
    // Current active runs only, not retained transcript/history or per-second updates.
    private readonly Dictionary<Guid, AgentRunActivityUpdate> _runActivities = [];
    private long _activitySequence;

    public AgentRunActivityUpdate? GetRunActivity(Guid sessionId)
    {
        while (true)
        {
            AgentRunActivityUpdate activity;
            lock (_activitySync)
                if (!_runActivities.TryGetValue(sessionId, out activity!)) return null;
            var checkpoint = GetLatestCheckpoint(sessionId);
            lock (_activitySync)
            {
                // Do not retire a newer generation using a checkpoint read for
                // the previous activity. No store call runs under this lock.
                if (!_runActivities.TryGetValue(sessionId, out var current)) return null;
                if (!ReferenceEquals(activity, current)) continue;
                if (checkpoint?.Status == AgentRunStatus.Running && checkpoint.RunRevision == activity.RunRevision)
                    return activity;
                _runActivities.Remove(sessionId);
                return null;
            }
        }
    }

    public void ReportRunActivity(Guid sessionId, long runRevision, AgentRunActivityKind kind, string text)
        => ReportRunActivity(sessionId, runRevision, kind, text, null);

    internal void ReportRunActivity(Guid sessionId, long runRevision, AgentRunActivityKind kind, string text, AgentRunRetryState? retry, Func<bool>? isCurrent = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var maximum = kind == AgentRunActivityKind.Reasoning ? 20_480 : 512;
        text = text.Trim();
        if (text.Length > maximum) text = text[..maximum];
        QueueSessionNotification(sessionId, () =>
        {
            if (isCurrent?.Invoke() == false) return;
            var checkpoint = GetLatestCheckpoint(sessionId);
            if (checkpoint?.Status != AgentRunStatus.Running || checkpoint.RunRevision != runRevision) return;
            AgentRunActivityUpdate update;
            lock (_activitySync)
            {
                _runActivities.TryGetValue(sessionId, out var previous);
                var reasoning = kind == AgentRunActivityKind.Reasoning ? text.Trim()
                    : previous?.RunRevision == runRevision ? previous.ReasoningText : null;
                update = new(runRevision, kind, text.Trim(), DateTimeOffset.UtcNow, retry, reasoning, ++_activitySequence);
                _runActivities[sessionId] = update;
            }
            DispatchRunActivityChanged(sessionId, update);
        });
    }
}
