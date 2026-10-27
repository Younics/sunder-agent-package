using System.Globalization;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed class AgentRunActivityState : IDisposable
{
    private readonly Func<bool> _isRunActive;
    private readonly Func<bool> _isFollowingLatest;
    private bool _waitingForApproval;
    private long _runRevision;
    private long _sequence;
    private ActivityPresentation _publishedPresentation;

    public AgentRunActivityState(
        Func<bool> isRunActive,
        Func<bool> isFollowingLatest,
        TimeSpan? quietDelay = null,
        Func<(bool Grouped, bool Running)>? groupedTools = null)
    {
        _isRunActive = isRunActive;
        _isFollowingLatest = isFollowingLatest;
        _publishedPresentation = CurrentPresentation;
    }

    public event Action? Changed;

    public string Text { get; private set; } = "Thinking";

    public bool IsReasoning { get; private set; }
    public string ReasoningText { get; private set; } = string.Empty;
    public AgentRunRetryState? Retry { get; private set; }

    public bool ShouldShow
        => (_isRunActive() || _waitingForApproval) && _isFollowingLatest();

    public void Reset()
    {
        Text = "Thinking";
        IsReasoning = false;
        ReasoningText = string.Empty;
        Retry = null;
        _runRevision = 0;
        _sequence = 0;
        _waitingForApproval = false;
        PublishIfChanged();
    }

    public void RebuildActiveToolCalls(IEnumerable<AgentTurnRecord> turns)
    {
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

        if (Retry is not null) return;
        SetText(ResolveActivityText(turn), isReasoning: false, notify: false);

        PublishIfChanged();
    }

    public void TrackCheckpoint(AgentRunCheckpointRecord? checkpoint)
    {
        if (checkpoint?.RunRevision != _runRevision)
        {
            Reset();
            _runRevision = checkpoint?.RunRevision ?? 0;
        }
        _waitingForApproval = checkpoint?.Status == AgentRunStatus.WaitingForApproval;
        if (checkpoint?.Status != AgentRunStatus.Running)
        {
            Retry = null;
            if (_waitingForApproval) SetText("Waiting for approval", isReasoning: false, notify: false);
            PublishIfChanged();
            return;
        }
        if (Retry is not null || _sequence > 0) { PublishIfChanged(); return; }
        SetText(ResolveActivityText(checkpoint), isReasoning: false);
        PublishIfChanged();
    }

    public void TrackUpdate(string? text, bool isReasoning)
    {
        if (isReasoning) ReasoningText = text?.Trim() ?? string.Empty;
        Retry = null;
        SetText(isReasoning || string.IsNullOrWhiteSpace(text) ? "Thinking" : text, isReasoning, notify: false);
        PublishIfChanged();
    }

    public void TrackStatus(long runRevision, long sequence, string text, bool isReasoning, string? reasoning, AgentRunRetryState? retry)
    {
        if (runRevision < _runRevision || runRevision == _runRevision && sequence > 0 && sequence <= _sequence) return;
        if (runRevision != _runRevision) Reset();
        _runRevision = runRevision;
        _sequence = sequence;
        if (isReasoning) ReasoningText = text.Trim();
        SetText(isReasoning ? "Thinking" : text, isReasoning, notify: false);
        if (reasoning is not null) ReasoningText = reasoning;
        Retry = retry;
        PublishIfChanged();
    }

    public void NotifyRunStateChanged()
    {
        if (!_isRunActive())
        {
            Retry = null;
        }

        PublishIfChanged();
    }

    public void NotifyFollowStateChanged() => PublishIfChanged();

    public void Dispose() { }

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
        => new(Text, IsReasoning, ShouldShow, ReasoningText, Retry);

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
            _ => turn.Role == AgentMessageRole.Assistant ? "Responding" : "Thinking",
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

    private readonly record struct ActivityPresentation(
        string Text,
        bool IsReasoning,
        bool ShouldShow,
        string ReasoningText,
        AgentRunRetryState? Retry);
}
