using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Sdk.Avalonia.Theming;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentToolInvocationRowViewModel : AgentTranscriptRowViewModel,
    ITranscriptToolExpansionOwner,
    IDisposable
{
    private readonly Func<AgentTurnRecord, AgentTurnItemRecord, IReadOnlyList<AgentChildSessionLinkViewModel>>? _childSessionLinksResolver;
    private readonly TranscriptToolDetailState _detailState;
    private AgentTurnRecord _turn;
    private AgentTurnItemRecord _currentItem;
    private TranscriptToolProjection _projection;
    private Guid? _resultTurnId;
    private bool _disposed;

    internal AgentToolInvocationRowViewModel(
        AgentTurnRecord turn,
        AgentTurnItemRecord item,
        TranscriptToolProjection projection,
        AgentToolPresentationService presentationService,
        Func<AgentTranscriptToolDetailRequest, CancellationToken, Task<AgentTranscriptToolDetailRecord?>> loadDetail,
        Func<AgentTurnRecord, AgentTurnItemRecord, IReadOnlyList<AgentChildSessionLinkViewModel>>? childSessionLinksResolver = null)
        : base(turn.TurnId, turn.CreatedAtUtc, projection.AnchorKey)
    {
        _turn = turn;
        _currentItem = item;
        _projection = projection;
        _childSessionLinksResolver = childSessionLinksResolver;
        _resultTurnId = item.Kind == AgentTurnItemKind.ToolResult ? turn.TurnId : null;
        _detailState = new TranscriptToolDetailState(
            projection,
            loadDetail,
            presentationService.Resolve);
        _detailState.Changed += OnDetailStateChanged;
        _detailState.Invalidated += OnDetailVisualInvalidated;
        ApplyHeaderProjection();
        RefreshChildSessionLinks();
        TranscriptToolDiagnostics.HeaderViewModelCreated();
    }

    public string ToolLabel => _projection.ToolLabel;
    public string StatusText => _projection.StatusText;
    public string StatusIconText => _projection.StatusIconText;
    public string HeaderDetailText => FirstNonBlank(_projection.ErrorSummary, _projection.HeaderHint) ?? string.Empty;
    public string SummaryText => string.IsNullOrWhiteSpace(HeaderDetailText)
        ? ToolLabel
        : $"{ToolLabel} {HeaderDetailText}";
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
    public Guid? ResultTurnId => _resultTurnId;
    public bool IsFailed => string.Equals(StatusText, "Failed", StringComparison.OrdinalIgnoreCase);
    public bool IsPrepared => string.Equals(StatusText, "Prepared", StringComparison.OrdinalIgnoreCase);
    public bool IsStarted => string.Equals(StatusText, "Started", StringComparison.OrdinalIgnoreCase);
    public bool IsRunning => IsPrepared
                             || IsStarted
                             || string.Equals(StatusText, "Running", StringComparison.OrdinalIgnoreCase);
    public bool IsCompleted => string.Equals(StatusText, "Completed", StringComparison.OrdinalIgnoreCase);
    public bool IsAmbiguous => string.Equals(StatusText, "Ambiguous", StringComparison.OrdinalIgnoreCase);
    public ObservableCollection<AgentChildSessionLinkViewModel> ChildSessionLinks { get; } = [];
    public AgentChildSessionLinkViewModel? ChildSessionLink => ChildSessionLinks.FirstOrDefault();
    public bool HasChildSessionLinks => ChildSessionLinks.Count > 0;
    public bool HasChildSessionLink => HasChildSessionLinks;
    public string ChildSessionDisplayText => ChildSessionLink is null
        ? string.Empty
        : string.IsNullOrWhiteSpace(HeaderDetailText)
            ? ChildSessionLink.DisplayText
            : HeaderDetailText;

    internal TranscriptToolExpansionRequest? BeginExpansion() => _detailState.BeginExpansion();

    internal Task<AgentTranscriptToolDetailRecord?> LoadDetailsAsync(
        TranscriptToolExpansionRequest request,
        CancellationToken cancellationToken)
        => _detailState.LoadAsync(request, cancellationToken);

    internal bool IsCurrentExpansion(TranscriptToolExpansionRequest request)
        => _detailState.IsCurrent(request);

    internal bool TryMaterializeDetails(
        TranscriptToolExpansionRequest request,
        AgentTranscriptToolDetailRecord? detail,
        out TranscriptToolDetailViewModel? details)
        => _detailState.TryMaterialize(request, detail, out details);

    internal bool CommitExpansion(TranscriptToolExpansionRequest request)
        => _detailState.CommitExpanded(request);

    internal void FailExpansion(TranscriptToolExpansionRequest request, Exception exception)
        => _detailState.Fail(request, exception);

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
        RefreshChildSessionLinks();
    }

    internal void UpdateProjection(
        AgentTurnRecord turn,
        AgentTurnItemRecord item,
        TranscriptToolProjection projection)
    {
        _turn = turn;
        _currentItem = item;
        UpdateProjection(projection);
        RefreshChildSessionLinks();
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
        OnPropertyChanged(nameof(IsPrepared));
        OnPropertyChanged(nameof(IsStarted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsCompleted));
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

    public void RefreshChildSessionLink() => RefreshChildSessionLinks();

    public void RefreshChildSessionLinks()
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
        OnPropertyChanged(nameof(ChildSessionLink));
        OnPropertyChanged(nameof(HasChildSessionLinks));
        OnPropertyChanged(nameof(HasChildSessionLink));
        OnPropertyChanged(nameof(ChildSessionDisplayText));
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
        => AgentThemeBrushes.Resolve(statusText switch
        {
            "Completed" => SunderThemeKeys.SuccessBrush,
            "Started" or "Running" => SunderThemeKeys.AccentBrush,
            "Ambiguous" => SunderThemeKeys.WarningBrush,
            "Prepared" => SunderThemeKeys.ForegroundMutedBrush,
            _ => SunderThemeKeys.DangerBrush,
        });

    private static IBrush? ResolveStateSoftBrush(string statusText)
        => AgentThemeBrushes.Resolve(statusText switch
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
