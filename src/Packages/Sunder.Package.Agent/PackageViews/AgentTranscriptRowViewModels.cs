using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveMarkdown.Avalonia;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.PackageViews;

public abstract class AgentTranscriptRowViewModel(Guid rowId, DateTimeOffset createdAtUtc) : ObservableObject
{
    public Guid RowId { get; } = rowId;

    public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;
}

public sealed class AgentTextTranscriptRowViewModel : AgentTranscriptRowViewModel
{
    private string _content;
    private ObservableStringBuilder _markdownBuilder;

    public AgentTextTranscriptRowViewModel(
        AgentTurnRecord turn,
        string content,
        IReadOnlyList<AgentTranscriptAttachmentViewModel>? attachments = null)
        : base(turn.TurnId, turn.CreatedAtUtc)
    {
        Role = turn.Role;
        RoleLabel = turn.Role.ToString().ToUpperInvariant();
        RoleGlyph = ResolveRoleGlyph(turn.Role);
        _content = content;
        _markdownBuilder = new ObservableStringBuilder().Append(content);
        if (attachments is not null)
        {
            foreach (var attachment in attachments)
            {
                Attachments.Add(attachment);
            }
        }
    }

    public AgentMessageRole Role { get; }

    public string RoleLabel { get; }

    public string RoleGlyph { get; }

    public bool IsUser => Role == AgentMessageRole.User;

    public bool IsNotUser => !IsUser;

    public bool IsAssistant => Role == AgentMessageRole.Assistant;

    public ObservableCollection<AgentTranscriptAttachmentViewModel> Attachments { get; } = [];

    public bool HasContent => !string.IsNullOrWhiteSpace(Content);

    public bool HasAttachments => Attachments.Count > 0;

    public string Content
    {
        get => _content;
        private set => SetProperty(ref _content, value);
    }

    public ObservableStringBuilder MarkdownBuilder
    {
        get => _markdownBuilder;
        private set => SetProperty(ref _markdownBuilder, value);
    }

    public void UpdateContent(string content)
    {
        if (string.Equals(Content, content, StringComparison.Ordinal))
        {
            return;
        }

        Content = content;
        MarkdownBuilder.Clear();
        MarkdownBuilder.Append(content);
        OnPropertyChanged(nameof(HasContent));
    }

    public void ReplaceAttachments(IReadOnlyList<AgentTranscriptAttachmentViewModel> attachments)
    {
        Attachments.Clear();
        foreach (var attachment in attachments)
        {
            Attachments.Add(attachment);
        }

        OnPropertyChanged(nameof(HasAttachments));
    }

    private static string ResolveRoleGlyph(AgentMessageRole role)
        => role switch
        {
            AgentMessageRole.User => "U",
            AgentMessageRole.Assistant => "A",
            AgentMessageRole.System => "S",
            AgentMessageRole.Tool => "T",
            _ => "?"
        };
}

public sealed partial class AgentActivityTranscriptRowViewModel : AgentTranscriptRowViewModel, IDisposable
{
    private readonly DispatcherTimer _timer;
    private string _activityTextBase;
    private int _tick = 3;

    public AgentActivityTranscriptRowViewModel(string activityTextBase = "Thinking")
        : base(Guid.Empty, DateTimeOffset.UtcNow)
    {
        _activityTextBase = string.IsNullOrWhiteSpace(activityTextBase) ? "Processing" : activityTextBase.Trim();
        _thinkingText = FormatThinkingText(_activityTextBase, _tick);
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(420),
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    public string RoleGlyph => "A";

    [ObservableProperty]
    private string _thinkingText = "Thinking...";

    public void SetActivityTextBase(string activityTextBase)
    {
        var normalized = string.IsNullOrWhiteSpace(activityTextBase) ? "Processing" : activityTextBase.Trim();
        if (string.Equals(_activityTextBase, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _activityTextBase = normalized;
        ThinkingText = FormatThinkingText(_activityTextBase, _tick);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _tick++;
        ThinkingText = FormatThinkingText(_activityTextBase, _tick);
    }

    private static string FormatThinkingText(string activityTextBase, int tick)
        => activityTextBase + new string('.', tick % 4);

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }
}
