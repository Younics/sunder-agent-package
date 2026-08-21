using System.Globalization;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed class AgentRunActivityState : IDisposable
{
    private static readonly TimeSpan DefaultQuietDelay = TimeSpan.FromMilliseconds(900);

    private readonly TimedStatusController _quietTimer = new();
    private readonly PresentationTaskScope _tasks = new(
        PresentationTaskScope.IgnoreExpectedFailure);
    private readonly TimeSpan _quietDelay;
    private readonly Func<bool> _isRunActive;
    private readonly Func<bool> _isFollowingLatest;
    private readonly Dictionary<Guid, ActiveToolCall> _activeToolCalls = [];
    private readonly HashSet<Guid> _observedToolResults = [];
    private bool _hasVisibleRunActivity;
    private bool _showAfterQuiet;
    private bool _disposed;
    private ActivityPresentation _publishedPresentation;

    public AgentRunActivityState(
        Func<bool> isRunActive,
        Func<bool> isFollowingLatest,
        TimeSpan? quietDelay = null)
    {
        _isRunActive = isRunActive;
        _isFollowingLatest = isFollowingLatest;
        _quietDelay = quietDelay ?? DefaultQuietDelay;
        _publishedPresentation = CurrentPresentation;
    }

    public event Action? Changed;

    public string Text { get; private set; } = "Thinking";

    public bool IsReasoning { get; private set; }

    public bool ShouldShow
        => _isRunActive()
           && _isFollowingLatest()
           && _activeToolCalls.Count == 0
           && (!_hasVisibleRunActivity || _showAfterQuiet);

    public void Reset()
    {
        _quietTimer.Cancel();
        Text = "Thinking";
        IsReasoning = false;
        _hasVisibleRunActivity = false;
        _showAfterQuiet = false;
        _activeToolCalls.Clear();
        _observedToolResults.Clear();
        PublishIfChanged();
    }

    public void RebuildActiveToolCalls(IEnumerable<AgentTurnRecord> turns)
    {
        _activeToolCalls.Clear();
        _observedToolResults.Clear();
        var items = turns.SelectMany(turn => turn.Items).ToArray();
        foreach (var item in items.Where(item => item.Kind == AgentTurnItemKind.ToolCall))
        {
            TrackActiveToolCall(item);
        }
        foreach (var item in items.Where(item => item.Kind == AgentTurnItemKind.ToolResult))
        {
            RemoveActiveToolCall(item);
        }
        PublishIfChanged();
    }

    public void TrackTurn(AgentTurnRecord turn, bool scheduleQuietTimer)
    {
        if (turn.Role == AgentMessageRole.User)
        {
            Reset();
            return;
        }

        if (!HasVisibleRunActivity(turn))
        {
            return;
        }

        TrackActiveToolCalls(turn);
        _hasVisibleRunActivity = true;
        _showAfterQuiet = !scheduleQuietTimer;
        SetText(ResolveActivityText(turn), isReasoning: false, notify: false);
        if (scheduleQuietTimer)
        {
            RestartQuietTimer();
        }

        PublishIfChanged();
    }

    public void TrackCheckpoint(AgentRunCheckpointRecord? checkpoint)
    {
        if (checkpoint?.Status != AgentRunStatus.Running)
        {
            _quietTimer.Cancel();
            _showAfterQuiet = false;
            PublishIfChanged();
            return;
        }

        SetText(ResolveActivityText(checkpoint), isReasoning: false);
        PublishIfChanged();
    }

    public void TrackUpdate(string? text, bool isReasoning)
    {
        SetText(string.IsNullOrWhiteSpace(text) ? "Thinking" : text, isReasoning, notify: false);
        _showAfterQuiet = true;
        PublishIfChanged();
    }

    public void NotifyRunStateChanged()
    {
        if (!_isRunActive())
        {
            _quietTimer.Cancel();
            _showAfterQuiet = false;
        }

        PublishIfChanged();
    }

    public void NotifyFollowStateChanged() => PublishIfChanged();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _quietTimer.Dispose();
        _tasks.Dispose();
    }

    private void RestartQuietTimer()
    {
        _quietTimer.Cancel();
        if (!_isRunActive())
        {
            return;
        }

        if (_quietDelay <= TimeSpan.Zero)
        {
            ShowAfterQuietPeriod();
            return;
        }

        _tasks.Run(_ => _quietTimer.ScheduleAsync(_quietDelay, ShowAfterQuietPeriod));
    }

    private void ShowAfterQuietPeriod()
    {
        if (!_isRunActive())
        {
            return;
        }

        _showAfterQuiet = true;
        PublishIfChanged();
    }

    private void SetText(string text, bool isReasoning, bool notify = true)
    {
        var normalized = string.IsNullOrWhiteSpace(text) ? "Processing" : text.Trim();
        if (string.Equals(Text, normalized, StringComparison.Ordinal)
            && IsReasoning == isReasoning)
        {
            return;
        }

        Text = normalized;
        IsReasoning = isReasoning;
        if (notify)
        {
            PublishIfChanged();
        }
    }

    private ActivityPresentation CurrentPresentation
        => new(Text, IsReasoning, ShouldShow);

    private void PublishIfChanged()
    {
        var current = CurrentPresentation;
        if (current == _publishedPresentation)
        {
            return;
        }

        _publishedPresentation = current;
        Changed?.Invoke();
    }

    private static bool HasVisibleRunActivity(AgentTurnRecord turn)
        => turn.Kind switch
        {
            AgentTurnKind.ToolCall => turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolCall),
            AgentTurnKind.ToolResult => turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolResult),
            _ => turn.Role == AgentMessageRole.Assistant
                 && !string.IsNullOrWhiteSpace(TranscriptRowProjector<object>.ExtractTextContent(turn)),
        };

    private static string ResolveActivityText(AgentTurnRecord turn)
        => turn.Kind switch
        {
            AgentTurnKind.ToolCall => ResolveToolCallActivityText(turn),
            AgentTurnKind.ToolResult => "Processing result",
            _ => turn.Role == AgentMessageRole.Assistant ? "Processing" : "Thinking",
        };

    private static string ResolveToolCallActivityText(AgentTurnRecord turn)
    {
        var toolId = turn.Items.FirstOrDefault(item => item.Kind == AgentTurnItemKind.ToolCall)?.ToolId;
        return string.IsNullOrWhiteSpace(toolId)
            ? "Running tool"
            : $"Running {HumanizeToolName(toolId)}";
    }

    private static string ResolveActivityText(AgentRunCheckpointRecord checkpoint)
    {
        var summary = checkpoint.Summary ?? string.Empty;
        if (TryExtractQuotedToolId(summary, "Executing approved tool '", out var toolId)
            || TryExtractQuotedToolId(summary, "Executing tool '", out toolId))
        {
            return $"Running {HumanizeToolName(toolId ?? string.Empty)}";
        }

        if (summary.Contains("completed. Continuing provider execution", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("continuing provider execution", StringComparison.OrdinalIgnoreCase))
        {
            return "Processing result";
        }

        if (summary.Contains("Provider execution is starting", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("User message queued", StringComparison.OrdinalIgnoreCase)
            || summary.Contains("Thinking", StringComparison.OrdinalIgnoreCase))
        {
            return "Thinking";
        }

        return "Processing";
    }

    private static bool TryExtractQuotedToolId(string text, string prefix, out string? toolId)
    {
        toolId = null;
        var start = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        start += prefix.Length;
        var end = text.IndexOf('\'', start);
        if (end <= start)
        {
            return false;
        }

        toolId = text[start..end];
        return !string.IsNullOrWhiteSpace(toolId);
    }

    private static string HumanizeToolName(string toolId)
    {
        if (McpToolCardPresentation.TryParseStableToolId(toolId, out _, out var toolName))
        {
            return McpToolCardPresentation.HumanizeToolName(toolName);
        }

        var parts = toolId.Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0
            ? "tool"
            : string.Join(' ', parts.Select(part =>
                CultureInfo.InvariantCulture.TextInfo.ToTitleCase(part.ToLowerInvariant())));
    }

    private void TrackActiveToolCalls(AgentTurnRecord turn)
    {
        foreach (var item in turn.Items.Where(item => item.Kind is AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult))
        {
            if (item.Kind == AgentTurnItemKind.ToolResult
                || item.ToolExecutionStatus is AgentToolExecutionStatus.Completed
                    or AgentToolExecutionStatus.Failed
                    or AgentToolExecutionStatus.Ambiguous)
            {
                RemoveActiveToolCall(item);
            }
            else
            {
                TrackActiveToolCall(item);
            }
        }
    }

    private void TrackActiveToolCall(AgentTurnItemRecord item)
    {
        if (item.ToolExecutionStatus is AgentToolExecutionStatus.Completed
            or AgentToolExecutionStatus.Failed
            or AgentToolExecutionStatus.Ambiguous)
        {
            _activeToolCalls.Remove(item.ItemId);
            return;
        }
        _activeToolCalls[item.ItemId] = new ActiveToolCall(
            item.ToolExecutionId,
            NormalizeCallId(item.CallId),
            item.ToolId);
    }

    private void RemoveActiveToolCall(AgentTurnItemRecord item)
    {
        if (item.Kind == AgentTurnItemKind.ToolResult && !_observedToolResults.Add(item.ItemId))
        {
            return;
        }
        if (_activeToolCalls.Remove(item.ItemId))
        {
            return;
        }

        var callId = NormalizeCallId(item.CallId);
        Guid? matchingItemId = item.ToolExecutionId is { } executionId
            ? _activeToolCalls
                .Where(pair => pair.Value.ExecutionId == executionId)
                .Select(pair => (Guid?)pair.Key)
                .FirstOrDefault()
            : null;
        if (matchingItemId is null && callId is not null)
        {
            matchingItemId = _activeToolCalls
                .Where(pair => string.Equals(pair.Value.CallId, callId, StringComparison.Ordinal))
                .Select(pair => (Guid?)pair.Key)
                .FirstOrDefault();
        }
        if (matchingItemId is null
            && item.ToolExecutionId is null
            && callId is null)
        {
            matchingItemId = _activeToolCalls
                .Where(pair => string.Equals(pair.Value.ToolId, item.ToolId, StringComparison.Ordinal))
                .Select(pair => (Guid?)pair.Key)
                .FirstOrDefault();
        }
        if (matchingItemId is { } itemId)
        {
            _activeToolCalls.Remove(itemId);
        }
    }

    private static string? NormalizeCallId(string? callId)
        => string.IsNullOrWhiteSpace(callId) ? null : callId;

    private sealed record ActiveToolCall(Guid? ExecutionId, string? CallId, string? ToolId);

    private readonly record struct ActivityPresentation(
        string Text,
        bool IsReasoning,
        bool ShouldShow);
}
