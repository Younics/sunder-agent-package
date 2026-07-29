using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveMarkdown.Avalonia;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Sdk.Avalonia.Theming;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public abstract partial class SubsessionTranscriptRowViewModel(Guid rowId, DateTimeOffset createdAtUtc, object anchorKey)
    : ObservableObject,
        ITranscriptAnchorItem
{
    public Guid RowId { get; } = rowId;
    public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;
    public object AnchorKey { get; } = anchorKey;
    internal virtual TranscriptAnchorItemRole AnchorRole => TranscriptAnchorItemRole.Transient;
    TranscriptAnchorItemRole ITranscriptAnchorItem.AnchorRole => AnchorRole;
    public virtual bool IsLayoutVisible => true;
    public virtual bool IsRowHitTestVisible => true;
    public virtual double MinimumLayoutHeight => 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NavigationHighlightHelpText))]
    private bool _isNavigationTargetHighlighted;

    [ObservableProperty]
    private bool _isNavigationTargetFading;

    public string? NavigationHighlightHelpText => IsNavigationTargetHighlighted
        ? "Search result target"
        : null;

    internal void PrimeNavigationHighlight()
    {
        IsNavigationTargetFading = false;
        IsNavigationTargetHighlighted = true;
    }

    internal void FadeNavigationHighlight()
    {
        if (IsNavigationTargetHighlighted)
        {
            IsNavigationTargetFading = true;
        }
    }

    internal void ClearNavigationHighlight()
    {
        IsNavigationTargetHighlighted = false;
        IsNavigationTargetFading = false;
    }
}

public sealed class SubsessionTextTranscriptRowViewModel : SubsessionTranscriptRowViewModel
{
    private string _content;
    private ObservableStringBuilder _markdownBuilder;

    public SubsessionTextTranscriptRowViewModel(AgentTurnRecord turn, string content)
        : base(turn.TurnId, turn.CreatedAtUtc, TranscriptRowAnchorKey.Text(turn.TurnId))
    {
        Role = turn.Role;
        RoleLabel = turn.Role.ToString().ToUpperInvariant();
        RoleGlyph = ResolveRoleGlyph(turn.Role);
        _content = content;
        _markdownBuilder = new ObservableStringBuilder().Append(content);
    }

    public AgentMessageRole Role { get; }
    public string RoleLabel { get; }
    public string RoleGlyph { get; }
    public bool IsUser => Role == AgentMessageRole.User;
    public bool IsNotUser => !IsUser;
    public bool IsAssistant => Role == AgentMessageRole.Assistant;
    public bool HasContent => !string.IsNullOrWhiteSpace(Content);

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

    private static string ResolveRoleGlyph(AgentMessageRole role)
        => role switch
        {
            AgentMessageRole.User => "U",
            AgentMessageRole.Assistant => "A",
            AgentMessageRole.System => "S",
            AgentMessageRole.Tool => "T",
            _ => "?",
        };
}

public sealed partial class SubsessionActivityTranscriptRowViewModel : SubsessionTranscriptRowViewModel, IDisposable
{
    private readonly IActivityTicker _ticker;
    private string _activityTextBase;
    private bool _isTickerSubscribed;
    private int _tick = 3;

    internal SubsessionActivityTranscriptRowViewModel(IActivityTicker ticker, string activityTextBase = "Thinking")
        : base(Guid.Empty, DateTimeOffset.UtcNow, TranscriptRowAnchorKey.Activity())
    {
        _ticker = ticker;
        _activityTextBase = string.IsNullOrWhiteSpace(activityTextBase) ? "Processing" : activityTextBase.Trim();
        _thinkingText = FormatThinkingText(_activityTextBase, _tick);
    }

    public string RoleGlyph => "A";
    internal override TranscriptAnchorItemRole AnchorRole => TranscriptAnchorItemRole.Persistent;
    public override bool IsLayoutVisible => IsVisible;
    public override bool IsRowHitTestVisible => IsVisible;
    public override double MinimumLayoutHeight => IsVisible ? 0 : 1;

    [ObservableProperty]
    private string _thinkingText = "Thinking...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLayoutVisible))]
    [NotifyPropertyChangedFor(nameof(IsRowHitTestVisible))]
    [NotifyPropertyChangedFor(nameof(MinimumLayoutHeight))]
    private bool _isVisible;

    partial void OnIsVisibleChanged(bool value)
    {
        if (value == _isTickerSubscribed)
        {
            return;
        }
        if (value)
        {
            _ticker.Tick += OnTick;
        }
        else
        {
            _ticker.Tick -= OnTick;
        }
        _isTickerSubscribed = value;
    }

    public void SetPresentation(string activityTextBase, bool isVisible)
    {
        SetActivityTextBase(activityTextBase);
        IsVisible = isVisible;
    }

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

    private void OnTick()
    {
        _tick++;
        ThinkingText = FormatThinkingText(_activityTextBase, _tick);
    }

    private static string FormatThinkingText(string activityTextBase, int tick)
        => activityTextBase + new string('.', tick % 4);

    public void Dispose()
    {
        if (_isTickerSubscribed)
        {
            _ticker.Tick -= OnTick;
            _isTickerSubscribed = false;
        }
    }
}

public sealed class SubsessionTranscriptTailSentinelRowViewModel : SubsessionTranscriptRowViewModel
{
    internal SubsessionTranscriptTailSentinelRowViewModel()
        : base(Guid.Empty, DateTimeOffset.MinValue, TranscriptRowAnchorKey.TailSentinel())
    {
    }

    internal override TranscriptAnchorItemRole AnchorRole => TranscriptAnchorItemRole.TailSentinel;
    public override bool IsRowHitTestVisible => false;
}

public sealed class SubsessionToolInvocationRowViewModel : SubsessionTranscriptRowViewModel,
    ITranscriptToolExpansionOwner,
    IDisposable
{
    private readonly Func<AgentTurnRecord, AgentTurnItemRecord, IReadOnlyList<SubsessionChildSessionLinkViewModel>>? _childSessionLinksResolver;
    private readonly TranscriptToolDetailState _detailState;
    private AgentTurnRecord _turn;
    private AgentTurnItemRecord _currentItem;
    private TranscriptToolProjection _projection;
    private Guid? _resultTurnId;
    private bool _disposed;

    internal SubsessionToolInvocationRowViewModel(
        AgentTurnRecord turn,
        AgentTurnItemRecord item,
        TranscriptToolProjection projection,
        TranscriptToolPresentationService presentationService,
        Func<AgentTranscriptToolDetailRequest, CancellationToken, Task<AgentTranscriptToolDetailRecord?>> loadDetail,
        Func<AgentTurnRecord, AgentTurnItemRecord, IReadOnlyList<SubsessionChildSessionLinkViewModel>>? childSessionLinksResolver = null)
        : base(turn.TurnId, turn.CreatedAtUtc, projection.AnchorKey)
    {
        _turn = turn;
        _currentItem = item;
        _projection = projection;
        _childSessionLinksResolver = childSessionLinksResolver;
        _resultTurnId = item.Kind == AgentTurnItemKind.ToolResult ? turn.TurnId : null;
        _detailState = new TranscriptToolDetailState(projection, loadDetail, presentationService.Resolve);
        _detailState.Changed += OnDetailStateChanged;
        _detailState.Invalidated += OnDetailVisualInvalidated;
        ApplyHeaderProjection();
        RefreshChildSessionLink();
        TranscriptToolDiagnostics.HeaderViewModelCreated();
    }

    public string ToolLabel => _projection.ToolLabel;
    public string StatusText => _projection.StatusText;
    public string StatusIconText => _projection.StatusIconText;
    public string HeaderDetailText => FirstNonBlank(_projection.ErrorSummary, _projection.HeaderHint) ?? string.Empty;
    public string SummaryText => string.IsNullOrWhiteSpace(HeaderDetailText) ? ToolLabel : $"{ToolLabel} {HeaderDetailText}";
    public bool HasHeaderDetail => !string.IsNullOrWhiteSpace(HeaderDetailText);
    public bool HasDetails => _projection.HasDetails;
    public long DetailRevision => _projection.DetailRevision;
    public Guid SessionId => _projection.SessionId;
    internal TranscriptToolExpansionState ExpansionState => _detailState.State;
    public bool IsExpanded
    {
        get => _detailState.IsExpanded;
        set
        {
            if (!value)
            {
                CollapseDetails();
            }
        }
    }
    public bool IsPreparing => _detailState.IsPreparing;
    public bool IsDetailLoadFailed => ExpansionState == TranscriptToolExpansionState.DetailLoadFailed;
    internal TranscriptToolDetailViewModel? ExpandedDetails => IsExpanded ? _detailState.Details : null;
    public bool HasMaterializedDetails => _detailState.Details is not null;
    public string DetailLoadFailureText => _detailState.FailureText;
    public string ExpandGlyph => IsExpanded ? "▴" : IsPreparing ? "…" : "▾";
    public string ExpansionAnnouncement => ExpansionState switch
    {
        TranscriptToolExpansionState.Preparing => $"Preparing {ToolLabel} details.",
        TranscriptToolExpansionState.Expanded => $"{ToolLabel} details expanded.",
        TranscriptToolExpansionState.DetailLoadFailed => $"{ToolLabel} details failed to load. {DetailLoadFailureText}",
        _ => $"{ToolLabel} details collapsed.",
    };
    public string HeaderAccessibilityText => string.Join(
        ". ",
        new[] { ToolLabel, StatusText, HeaderDetailText, ExpansionAnnouncement }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    public IBrush? StateBrush { get; private set; }
    public IBrush? StateSoftBrush { get; private set; }
    public bool IsAmbiguous => string.Equals(StatusText, "Ambiguous", StringComparison.OrdinalIgnoreCase);
    public Guid? ResultTurnId => _resultTurnId;
    public ObservableCollection<SubsessionChildSessionLinkViewModel> ChildSessionLinks { get; } = [];
    public bool HasChildSessionLinks => ChildSessionLinks.Count > 0;

    internal TranscriptToolExpansionRequest? BeginExpansion() => _detailState.BeginExpansion();
    internal Task<AgentTranscriptToolDetailRecord?> LoadDetailsAsync(
        TranscriptToolExpansionRequest request,
        CancellationToken cancellationToken)
        => _detailState.LoadAsync(request, cancellationToken);
    internal bool IsCurrentExpansion(TranscriptToolExpansionRequest request) => _detailState.IsCurrent(request);
    internal bool TryMaterializeDetails(
        TranscriptToolExpansionRequest request,
        AgentTranscriptToolDetailRecord? detail,
        out TranscriptToolDetailViewModel? details)
        => _detailState.TryMaterialize(request, detail, out details);
    internal bool CommitExpansion(TranscriptToolExpansionRequest request) => _detailState.CommitExpanded(request);
    internal void FailExpansion(TranscriptToolExpansionRequest request, Exception exception) => _detailState.Fail(request, exception);
    internal void CollapseDetails() => _detailState.Collapse();

    event Action? ITranscriptToolExpansionOwner.DetailVisualInvalidated
    {
        add => DetailVisualInvalidated += value;
        remove => DetailVisualInvalidated -= value;
    }

    private event Action? DetailVisualInvalidated;

    void ITranscriptToolExpansionOwner.OnDetailVisualInvalidated() => CollapseDetails();

    private void OnDetailVisualInvalidated() => DetailVisualInvalidated?.Invoke();

    internal void ApplyResult(
        AgentTurnRecord turn,
        AgentTurnItemRecord item,
        TranscriptToolProjection projection)
    {
        _resultTurnId = turn.TurnId;
        _currentItem = item;
        UpdateProjection(projection);
        OnPropertyChanged(nameof(ResultTurnId));
        RefreshChildSessionLink();
    }

    internal void UpdateProjection(
        AgentTurnRecord turn,
        AgentTurnItemRecord item,
        TranscriptToolProjection projection)
    {
        _turn = turn;
        _currentItem = item;
        UpdateProjection(projection);
        RefreshChildSessionLink();
    }

    private void UpdateProjection(TranscriptToolProjection projection)
    {
        _projection = projection;
        _detailState.UpdateProjection(projection);
        ApplyHeaderProjection();
    }

    private void ApplyHeaderProjection()
    {
        StateBrush = ResolveStateBrush(StatusText);
        StateSoftBrush = ResolveStateSoftBrush(StatusText);
        OnPropertyChanged(nameof(ToolLabel));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusIconText));
        OnPropertyChanged(nameof(HeaderDetailText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasHeaderDetail));
        OnPropertyChanged(nameof(HasDetails));
        OnPropertyChanged(nameof(DetailRevision));
        OnPropertyChanged(nameof(SessionId));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(StateSoftBrush));
        OnPropertyChanged(nameof(IsAmbiguous));
        OnPropertyChanged(nameof(HeaderAccessibilityText));
    }

    private void OnDetailStateChanged()
    {
        OnPropertyChanged(nameof(ExpansionState));
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(IsPreparing));
        OnPropertyChanged(nameof(IsDetailLoadFailed));
        OnPropertyChanged(nameof(ExpandedDetails));
        OnPropertyChanged(nameof(HasMaterializedDetails));
        OnPropertyChanged(nameof(DetailLoadFailureText));
        OnPropertyChanged(nameof(ExpandGlyph));
        OnPropertyChanged(nameof(ExpansionAnnouncement));
        OnPropertyChanged(nameof(HeaderAccessibilityText));
    }

    public void RefreshChildSessionLink()
    {
        var links = _childSessionLinksResolver?.Invoke(_turn, _currentItem) ?? [];
        var desiredSessionIds = links.Select(link => link.SessionId).ToHashSet();
        for (var index = ChildSessionLinks.Count - 1; index >= 0; index--)
        {
            if (!desiredSessionIds.Contains(ChildSessionLinks[index].SessionId))
            {
                ChildSessionLinks.RemoveAt(index);
            }
        }
        for (var index = 0; index < links.Count; index++)
        {
            var link = links[index];
            var existingIndex = FindChildSessionLinkIndex(link.SessionId);
            if (existingIndex < 0)
            {
                ChildSessionLinks.Insert(index, link);
                continue;
            }
            var existing = ChildSessionLinks[existingIndex];
            existing.Update(link.Title, link.Subtitle, ParseLinkStatus(link.StatusText));
            if (existingIndex != index)
            {
                ChildSessionLinks.Move(existingIndex, index);
            }
        }
        OnPropertyChanged(nameof(HasChildSessionLinks));
    }

    private int FindChildSessionLinkIndex(Guid sessionId)
    {
        for (var index = 0; index < ChildSessionLinks.Count; index++)
        {
            if (ChildSessionLinks[index].SessionId == sessionId)
            {
                return index;
            }
        }
        return -1;
    }

    private static AgentRunStatus ParseLinkStatus(string statusText)
        => string.Equals(statusText, "Done", StringComparison.OrdinalIgnoreCase)
            ? AgentRunStatus.Completed
            : Enum.TryParse<AgentRunStatus>(statusText, ignoreCase: true, out var status)
                ? status
                : AgentRunStatus.Idle;

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static IBrush? ResolveStateBrush(string statusText)
        => SubagentThemeBrushes.Resolve(statusText switch
        {
            "Completed" => SunderThemeKeys.SuccessBrush,
            "Started" or "Running" => SunderThemeKeys.AccentBrush,
            "Ambiguous" => SunderThemeKeys.WarningBrush,
            "Prepared" => SunderThemeKeys.ForegroundMutedBrush,
            _ => SunderThemeKeys.DangerBrush,
        });

    private static IBrush? ResolveStateSoftBrush(string statusText)
        => SubagentThemeBrushes.Resolve(statusText switch
        {
            "Completed" => SunderThemeKeys.SuccessSoftBrush,
            "Started" or "Running" => SunderThemeKeys.InfoSoftBrush,
            "Ambiguous" => SunderThemeKeys.WarningSoftBrush,
            "Prepared" => SunderThemeKeys.SurfacePopoverBrush,
            _ => SunderThemeKeys.DangerSoftBrush,
        });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _detailState.Changed -= OnDetailStateChanged;
        _detailState.Invalidated -= OnDetailVisualInvalidated;
        _detailState.Dispose();
        DetailVisualInvalidated = null;
        ChildSessionLinks.Clear();
        TranscriptToolDiagnostics.HeaderViewModelDestroyed();
    }
}

public sealed partial class SubsessionChildSessionLinkViewModel : ObservableObject
{
    public SubsessionChildSessionLinkViewModel(
        Guid sessionId,
        string title,
        string subtitle,
        AgentRunStatus status = AgentRunStatus.Idle)
    {
        SessionId = sessionId;
        _title = title;
        _subtitle = subtitle;
        ApplyStatus(status);
    }

    public Guid SessionId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string _subtitle;

    [ObservableProperty]
    private string _statusText = "Idle";

    [ObservableProperty]
    private string _statusIconText = "!";

    [ObservableProperty]
    private IBrush? _stateBrush;

    [ObservableProperty]
    private IBrush? _stateSoftBrush;

    public string DisplayText => string.IsNullOrWhiteSpace(Subtitle) ? Title : $"{Subtitle} · {Title}";

    public void Update(string title, string subtitle, AgentRunStatus status)
    {
        Title = title;
        Subtitle = subtitle;
        ApplyStatus(status);
    }

    private void ApplyStatus(AgentRunStatus status)
    {
        StatusText = status == AgentRunStatus.Completed ? "Done" : status.ToString();
        StatusIconText = status switch
        {
            AgentRunStatus.Completed => "✓",
            AgentRunStatus.Running => "i",
            _ => "!",
        };
        StateBrush = SubagentThemeBrushes.Resolve(status switch
        {
            AgentRunStatus.Completed => SunderThemeKeys.SuccessBrush,
            AgentRunStatus.Running => SunderThemeKeys.AccentBrush,
            AgentRunStatus.Failed => SunderThemeKeys.DangerBrush,
            AgentRunStatus.Interrupted or AgentRunStatus.Stopped => SunderThemeKeys.WarningBrush,
            _ => SunderThemeKeys.ForegroundMutedBrush,
        });
        StateSoftBrush = SubagentThemeBrushes.Resolve(status switch
        {
            AgentRunStatus.Completed => SunderThemeKeys.SuccessSoftBrush,
            AgentRunStatus.Running => SunderThemeKeys.InfoSoftBrush,
            AgentRunStatus.Failed => SunderThemeKeys.DangerSoftBrush,
            AgentRunStatus.Interrupted or AgentRunStatus.Stopped => SunderThemeKeys.WarningSoftBrush,
            _ => SunderThemeKeys.SurfacePopoverBrush,
        });
    }
}
