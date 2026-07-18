using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveMarkdown.Avalonia;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

public abstract class AgentTranscriptRowViewModel(Guid rowId, DateTimeOffset createdAtUtc, object anchorKey)
    : ObservableObject,
        ITranscriptAnchorItem
{
    public Guid RowId { get; } = rowId;

    public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;

    public object AnchorKey { get; } = anchorKey;
}

public sealed class AgentTextTranscriptRowViewModel : AgentTranscriptRowViewModel
{
    private string _content;
    private ObservableStringBuilder _markdownBuilder;

    public AgentTextTranscriptRowViewModel(
        AgentTurnRecord turn,
        string content,
        IReadOnlyList<AgentTranscriptAttachmentViewModel>? attachments = null,
        string? senderDisplayName = null)
        : base(turn.TurnId, turn.CreatedAtUtc, TranscriptRowAnchorKey.Text(turn.TurnId))
    {
        Role = turn.Role;
        RoleLabel = turn.Role.ToString().ToUpperInvariant();
        RoleGlyph = ResolveRoleGlyph(turn.Role);
        SenderDisplayName = ResolveSenderDisplayName(turn.Role, senderDisplayName);
        SentAtText = FormatSentAtText(turn.CreatedAtUtc);
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

    public bool ShowMessageHeader => IsUser || IsAssistant;

    public string SenderDisplayName { get; }

    public string SentAtText { get; }

    public string MessageHeaderText => $"{SenderDisplayName} · {SentAtText}";

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
        var previousContent = Content;
        if (string.Equals(previousContent, content, StringComparison.Ordinal))
        {
            return;
        }

        Content = content;
        if (content.StartsWith(previousContent, StringComparison.Ordinal))
        {
            var suffix = content[previousContent.Length..];
            if (suffix.Length > 0)
            {
                MarkdownBuilder.Append(suffix);
            }
        }
        else
        {
            MarkdownBuilder.Clear();
            MarkdownBuilder.Append(content);
        }

        OnPropertyChanged(nameof(HasContent));
    }

    public void ReplaceAttachments(IReadOnlyList<AgentTranscriptAttachmentViewModel> attachments)
    {
        if (Attachments.Count == attachments.Count
            && Attachments.Select(attachment => attachment.Metadata)
                .SequenceEqual(attachments.Select(attachment => attachment.Metadata)))
        {
            return;
        }

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

    private static string ResolveSenderDisplayName(AgentMessageRole role, string? senderDisplayName)
        => role switch
        {
            AgentMessageRole.User => "You",
            AgentMessageRole.Assistant => string.IsNullOrWhiteSpace(senderDisplayName) ? "Agent" : senderDisplayName.Trim(),
            _ => role.ToString()
        };

    private static string FormatSentAtText(DateTimeOffset createdAtUtc)
    {
        var localCreatedAt = createdAtUtc.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        var createdDate = localCreatedAt.Date;
        var timeText = localCreatedAt.ToString("t", CultureInfo.CurrentCulture);

        if (createdDate == today)
        {
            return $"Today, {timeText}";
        }

        if (createdDate == today.AddDays(-1))
        {
            return $"Yesterday, {timeText}";
        }

        var dateFormat = localCreatedAt.Year == today.Year ? "MMM d" : "MMM d, yyyy";
        return $"{localCreatedAt.ToString(dateFormat, CultureInfo.CurrentCulture)}, {timeText}";
    }
}

public sealed partial class AgentActivityTranscriptRowViewModel : AgentTranscriptRowViewModel, IDisposable
{
    private readonly IActivityTicker _ticker;
    private string _activityTextBase;
    private bool _isReasoningActivity;
    private bool _animateActivityText = true;
    private int _tick = 3;

    public AgentActivityTranscriptRowViewModel(
        string activityTextBase = "Thinking",
        bool isReasoningActivity = false)
        : this(NullActivityTicker.Instance, activityTextBase, isReasoningActivity)
    {
    }

    internal AgentActivityTranscriptRowViewModel(
        IActivityTicker ticker,
        string activityTextBase = "Thinking",
        bool isReasoningActivity = false)
        : base(Guid.Empty, DateTimeOffset.UtcNow, TranscriptRowAnchorKey.Activity())
    {
        _ticker = ticker;
        _activityTextBase = string.IsNullOrWhiteSpace(activityTextBase) ? "Processing" : activityTextBase.Trim();
        _isReasoningActivity = isReasoningActivity;
        ApplyActivityTextBase(_activityTextBase, isReasoningActivity);
        _ticker.Tick += OnTick;
    }

    public string RoleGlyph => "A";

    [ObservableProperty]
    private string _thinkingText = "Thinking...";

    public void SetActivityTextBase(string activityTextBase, bool isReasoningActivity = false)
    {
        var normalized = string.IsNullOrWhiteSpace(activityTextBase) ? "Processing" : activityTextBase.Trim();
        if (string.Equals(_activityTextBase, normalized, StringComparison.Ordinal)
            && _isReasoningActivity == isReasoningActivity)
        {
            return;
        }

        _activityTextBase = normalized;
        _isReasoningActivity = isReasoningActivity;
        ApplyActivityTextBase(_activityTextBase, isReasoningActivity);
    }

    private void OnTick()
    {
        if (!_animateActivityText)
        {
            return;
        }

        _tick++;
        ThinkingText = _isReasoningActivity
            ? FormatReasoningActivityText(CreateCompactReasoningText(_activityTextBase), _tick)
            : FormatThinkingText(ResolveActivityText(_activityTextBase), _tick);
    }

    private static string FormatThinkingText(string activityTextBase, int tick)
        => activityTextBase + FormatActivityDots(tick);

    private static string FormatReasoningActivityText(string activityText, int tick)
        => string.IsNullOrWhiteSpace(activityText)
            ? FormatThinkingText("Thinking", tick)
            : activityText + " " + FormatActivityDots(tick);

    private static string FormatActivityDots(int tick)
        => new string('.', ((tick + 2) % 3) + 1);

    private void ApplyActivityTextBase(string activityTextBase, bool isReasoningActivity)
    {
        if (isReasoningActivity)
        {
            ApplyReasoningActivityText(activityTextBase);
            return;
        }

        var activityText = ResolveActivityText(activityTextBase);
        _animateActivityText = ShouldAnimateActivityText(activityTextBase);
        ThinkingText = _animateActivityText ? FormatThinkingText(activityText, _tick) : activityText;
    }

    private void ApplyReasoningActivityText(string markdown)
    {
        _animateActivityText = true;
        var normalizedMarkdown = string.IsNullOrWhiteSpace(markdown) ? "Thinking" : markdown.Trim();
        ThinkingText = FormatReasoningActivityText(CreateCompactReasoningText(normalizedMarkdown), _tick);
    }

    private static string ResolveActivityText(string activityTextBase)
    {
        if (activityTextBase.StartsWith("Running ", StringComparison.OrdinalIgnoreCase))
        {
            return activityTextBase;
        }

        if (activityTextBase.Contains("result", StringComparison.OrdinalIgnoreCase))
        {
            return "Reviewing result";
        }

        if (activityTextBase.Contains("Processing", StringComparison.OrdinalIgnoreCase))
        {
            return "Processing result";
        }

        return string.IsNullOrWhiteSpace(activityTextBase) ? "Thinking" : activityTextBase.Trim();
    }

    private static bool ShouldAnimateActivityText(string activityTextBase)
        => activityTextBase.Equals("Thinking", StringComparison.OrdinalIgnoreCase)
           || activityTextBase.StartsWith("Running ", StringComparison.OrdinalIgnoreCase)
           || activityTextBase.StartsWith("Processing", StringComparison.OrdinalIgnoreCase)
           || activityTextBase.StartsWith("Reviewing", StringComparison.OrdinalIgnoreCase);

    private static string CreateCompactReasoningText(string markdown)
        => StripMarkdown(markdown);

    private static string StripMarkdown(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = Regex.Replace(text, @"!\[([^\]]*)\]\([^)]+\)", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return string.Join(
            Environment.NewLine,
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(StripMarkdownLine)
                .Select(line => Regex.Replace(line, @"[*_`~]+", string.Empty))
                .Select(NormalizeWhitespace)
                .Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string StripMarkdownLine(string line)
    {
        var text = line.Trim();
        while (text.StartsWith('#'))
        {
            text = text[1..].TrimStart();
        }

        while (text.StartsWith('>'))
        {
            text = text[1..].TrimStart();
        }

        text = Regex.Replace(text, @"^[-*+]\s+", string.Empty);
        text = Regex.Replace(text, @"^\d+[.)]\s+", string.Empty);
        return text;
    }

    private static string NormalizeWhitespace(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public void Dispose()
    {
        _ticker.Tick -= OnTick;
    }
}
