using System.Globalization;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed class AgentRunActivityState : IDisposable
{
    private static readonly TimeSpan DefaultQuietDelay = TimeSpan.FromMilliseconds(900);

    private readonly TimedStatusController _quietTimer = new();
    private readonly PresentationTaskScope _tasks = new();
    private readonly TimeSpan _quietDelay;
    private readonly Func<bool> _isRunActive;
    private readonly Func<bool> _isFollowingLatest;
    private bool _hasVisibleRunActivity;
    private bool _showAfterQuiet;
    private bool _disposed;

    public AgentRunActivityState(
        Func<bool> isRunActive,
        Func<bool> isFollowingLatest,
        TimeSpan? quietDelay = null)
    {
        _isRunActive = isRunActive;
        _isFollowingLatest = isFollowingLatest;
        _quietDelay = quietDelay ?? DefaultQuietDelay;
    }

    public event Action? Changed;

    public string Text { get; private set; } = "Thinking";

    public bool IsReasoning { get; private set; }

    public bool ShouldShow
        => _isRunActive()
           && _isFollowingLatest()
           && (!_hasVisibleRunActivity || _showAfterQuiet);

    public void Reset()
    {
        _quietTimer.Cancel();
        Text = "Thinking";
        IsReasoning = false;
        _hasVisibleRunActivity = false;
        _showAfterQuiet = false;
        Changed?.Invoke();
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

        _hasVisibleRunActivity = true;
        _showAfterQuiet = !scheduleQuietTimer;
        SetText(ResolveActivityText(turn), isReasoning: false, notify: false);
        if (scheduleQuietTimer)
        {
            RestartQuietTimer();
        }

        Changed?.Invoke();
    }

    public void TrackCheckpoint(AgentRunCheckpointRecord? checkpoint)
    {
        if (checkpoint?.Status != AgentRunStatus.Running)
        {
            _quietTimer.Cancel();
            _showAfterQuiet = false;
            Changed?.Invoke();
            return;
        }

        SetText(ResolveActivityText(checkpoint), isReasoning: false);
    }

    public void TrackUpdate(string? text, bool isReasoning)
    {
        SetText(string.IsNullOrWhiteSpace(text) ? "Thinking" : text, isReasoning, notify: false);
        _showAfterQuiet = true;
        Changed?.Invoke();
    }

    public void NotifyRunStateChanged()
    {
        if (!_isRunActive())
        {
            _quietTimer.Cancel();
            _showAfterQuiet = false;
        }

        Changed?.Invoke();
    }

    public void NotifyFollowStateChanged() => Changed?.Invoke();

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

        _tasks.Run(_quietTimer.ScheduleAsync(_quietDelay, ShowAfterQuietPeriod));
    }

    private void ShowAfterQuietPeriod()
    {
        if (!_isRunActive() || !_isFollowingLatest())
        {
            return;
        }

        _showAfterQuiet = true;
        Changed?.Invoke();
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
            Changed?.Invoke();
        }
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
        var parts = toolId.Split(['_', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0
            ? "tool"
            : string.Join(' ', parts.Select(part =>
                CultureInfo.InvariantCulture.TextInfo.ToTitleCase(part.ToLowerInvariant())));
    }
}
