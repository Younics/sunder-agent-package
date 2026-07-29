using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.HistorySearch;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentHistorySearchViewModel
{
    partial void OnQueryTextChanged(string value)
    {
        NotifyQueryStateChanged();
        ScheduleSearch(QueryDebounce);
    }

    partial void OnSelectedScopeChanged(HistoryChoiceViewModel? value)
    {
        OnPropertyChanged(nameof(IsSpecificWorkspaceScope));
        if (_suppressSelectionChanges)
        {
            return;
        }
        ApplyFilterMutation(() =>
        {
            if (IsSpecificWorkspaceScope && SelectedWorkspace is null)
            {
                SelectedWorkspace = WorkspaceOptions.FirstOrDefault(option => string.Equals(
                                        option.Id,
                                        _currentWorkspaceId,
                                        StringComparison.OrdinalIgnoreCase))
                                    ?? WorkspaceOptions.FirstOrDefault();
            }
            RebuildSessionOptions(SelectedSession?.Id);
            UpdateScopeContext();
        });
    }

    partial void OnSelectedWorkspaceChanged(HistoryFilterOptionViewModel? value)
    {
        if (_suppressSelectionChanges || !IsSpecificWorkspaceScope)
        {
            return;
        }
        ApplyFilterMutation(() =>
        {
            RebuildSessionOptions(SelectedSession?.Id);
            UpdateScopeContext();
        });
    }

    partial void OnSelectedSessionChanged(HistoryFilterOptionViewModel? value)
    {
        OnPropertyChanged(nameof(IsSpecificSessionSelected));
        FilterPropertyChanged();
    }

    partial void OnSelectedProfileChanged(HistoryFilterOptionViewModel? value) => FilterPropertyChanged();
    partial void OnSelectedRoleChanged(HistoryChoiceViewModel? value) => FilterPropertyChanged();
    partial void OnSelectedActivityChanged(HistoryChoiceViewModel? value) => FilterPropertyChanged();

    partial void OnSelectedWhenChanged(HistoryChoiceViewModel? value)
    {
        OnPropertyChanged(nameof(IsCustomRange));
        UpdateDateValidation();
        FilterPropertyChanged();
    }

    partial void OnFromDateChanged(DateTimeOffset? value)
    {
        UpdateDateValidation();
        if (IsCustomRange)
        {
            FilterPropertyChanged();
        }
    }

    partial void OnToDateChanged(DateTimeOffset? value)
    {
        UpdateDateValidation();
        if (IsCustomRange)
        {
            FilterPropertyChanged();
        }
    }

    partial void OnIncludeChildSessionsChanged(bool value) => FilterPropertyChanged();

    partial void OnIsAdvancedExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(AdvancedChevron));
        OnPropertyChanged(nameof(AdvancedAutomationHelpText));
        if (value && !_advancedFiltersLoaded && !IsAdvancedLoading)
        {
            _ = LoadAdvancedFiltersAsync();
        }
        else if (!value)
        {
            CancelAdvancedFilterLoad();
            IsAdvancedLoading = false;
        }
    }

    partial void OnIsSearchingChanged(bool value) => NotifyResultStateChanged();
    partial void OnIsInitialLoadingChanged(bool value) => NotifyResultStateChanged();
    partial void OnIsUnavailableChanged(bool value) => NotifyResultStateChanged();
    partial void OnSearchErrorChanged(string value) => NotifyResultStateChanged();
    partial void OnNavigationErrorChanged(string value) => NotifyResultStateChanged();
    partial void OnRuntimeNoticeChanged(string value) => NotifyResultStateChanged();
    partial void OnDateValidationMessageChanged(string value)
        => OnPropertyChanged(nameof(HasDateValidation));

    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedExpanded = !IsAdvancedExpanded;

    [RelayCommand]
    private void ResetFilters()
    {
        _suppressSelectionChanges = true;
        try
        {
            SelectedScope = ScopeOptions.First(static option => option.Id == CurrentWorkspaceScopeId);
            SelectedWorkspace = WorkspaceOptions.FirstOrDefault(option => string.Equals(
                                    option.Id,
                                    _currentWorkspaceId,
                                    StringComparison.OrdinalIgnoreCase))
                                ?? WorkspaceOptions.FirstOrDefault();
            RebuildSessionOptions(selectedSessionId: null);
            SelectedProfile = ProfileOptions.FirstOrDefault();
            SelectedRole = RoleOptions[0];
            SelectedActivity = ActivityOptions[0];
            SelectedWhen = WhenOptions[0];
            FromDate = null;
            ToDate = null;
            IncludeChildSessions = true;
        }
        finally
        {
            _suppressSelectionChanges = false;
        }
        NotifyFilterShapeChanged();
        ScheduleSearch(TimeSpan.Zero);
    }

    [RelayCommand]
    private void RemoveFilter(HistoryActiveFilterChipViewModel? filter)
    {
        if (filter is null)
        {
            return;
        }
        _suppressSelectionChanges = true;
        try
        {
            switch (filter.Key)
            {
                case "scope":
                    SelectedScope = ScopeOptions.First(static option => option.Id == CurrentWorkspaceScopeId);
                    RebuildSessionOptions(SelectedSession?.Id);
                    break;
                case "session":
                    SelectedSession = SessionOptions.FirstOrDefault();
                    IncludeChildSessions = true;
                    break;
                case "profile":
                    SelectedProfile = ProfileOptions.FirstOrDefault();
                    break;
                case "role":
                    SelectedRole = RoleOptions[0];
                    break;
                case "activity":
                    SelectedActivity = ActivityOptions[0];
                    break;
                case "when":
                    SelectedWhen = WhenOptions[0];
                    FromDate = null;
                    ToDate = null;
                    break;
                case "children":
                    IncludeChildSessions = true;
                    break;
                default:
                    return;
            }
        }
        finally
        {
            _suppressSelectionChanges = false;
        }
        NotifyFilterShapeChanged();
        ScheduleSearch(TimeSpan.Zero);
    }

    private void FilterPropertyChanged()
    {
        if (_suppressSelectionChanges)
        {
            return;
        }
        NotifyFilterShapeChanged();
        ScheduleSearch(TimeSpan.Zero);
    }

    private void ApplyFilterMutation(Action mutation)
    {
        _suppressSelectionChanges = true;
        try
        {
            mutation();
        }
        finally
        {
            _suppressSelectionChanges = false;
        }
        NotifyFilterShapeChanged();
        ScheduleSearch(TimeSpan.Zero);
    }

    private void NotifyFilterShapeChanged()
    {
        OnPropertyChanged(nameof(IsSpecificWorkspaceScope));
        OnPropertyChanged(nameof(IsSpecificSessionSelected));
        OnPropertyChanged(nameof(IsCustomRange));
        UpdateScopeContext();
        UpdateDateValidation();
        UpdateActiveFilters();
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
    }

    private void RebuildSessionOptions(string? selectedSessionId)
    {
        var workspaceId = SelectedScope?.Id switch
        {
            CurrentWorkspaceScopeId => _currentWorkspaceId,
            SpecificWorkspaceScopeId => SelectedWorkspace?.Id,
            _ => null,
        };
        var options = _allSessionOptions.Where(option => workspaceId is null
                                                         || string.Equals(
                                                             option.ParentId,
                                                             workspaceId,
                                                             StringComparison.OrdinalIgnoreCase));
        ReplaceOptions(SessionOptions, new[] { new HistoryFilterOptionViewModel(string.Empty, "All sessions") }
            .Concat(options));
        SelectedSession = SessionOptions.FirstOrDefault(option => string.Equals(
                              option.Id,
                              selectedSessionId,
                              StringComparison.OrdinalIgnoreCase))
                          ?? SessionOptions.FirstOrDefault();
        OnPropertyChanged(nameof(IsSpecificSessionSelected));
    }

    private void UpdateScopeContext()
    {
        ScopeContextText = SelectedScope?.Id switch
        {
            AllWorkspacesScopeId => "All workspaces",
            SpecificWorkspaceScopeId when SelectedWorkspace is not null =>
                $"Workspace: {SelectedWorkspace.DisplayName}",
            SpecificWorkspaceScopeId => "Choose a workspace",
            _ when !string.IsNullOrWhiteSpace(_currentWorkspaceName) =>
                $"Current workspace: {_currentWorkspaceName}",
            _ when !string.IsNullOrWhiteSpace(_currentWorkspaceId) => "Current workspace",
            _ => "No current workspace selected",
        };
    }

    private void UpdateDateValidation()
    {
        DateValidationMessage = IsCustomRange
                                && FromDate is { } from
                                && ToDate is { } to
                                && from.Date > to.Date
            ? "Start is after end. The dates are normalized from the earlier day through the later day."
            : string.Empty;
    }

    private void UpdateActiveFilters()
    {
        var filters = new List<HistoryActiveFilterChipViewModel>();
        switch (SelectedScope?.Id)
        {
            case AllWorkspacesScopeId:
                filters.Add(new("scope", "All workspaces"));
                break;
            case SpecificWorkspaceScopeId:
                filters.Add(new("scope", SelectedWorkspace is null
                    ? "Specific workspace"
                    : $"Workspace: {SelectedWorkspace.DisplayName}"));
                break;
        }
        if (IsSpecificSessionSelected)
        {
            filters.Add(new("session", $"Session: {SelectedSession!.DisplayName}"));
        }
        if (!string.IsNullOrWhiteSpace(SelectedProfile?.Id))
        {
            filters.Add(new("profile", $"Profile: {SelectedProfile.DisplayName}"));
        }
        if (SelectedRole?.Id != "Any")
        {
            filters.Add(new("role", $"Role: {SelectedRole?.DisplayName}"));
        }
        if (SelectedActivity?.Id != "None")
        {
            filters.Add(new("activity", $"Activity: {SelectedActivity?.DisplayName}"));
        }
        if (SelectedWhen?.Id != AnyTimeId)
        {
            filters.Add(new("when", CreateWhenFilterLabel()));
        }
        if (IsSpecificSessionSelected && !IncludeChildSessions)
        {
            filters.Add(new("children", "Child sessions excluded"));
        }
        ReplaceOptions(ActiveFilters, filters);
        OnPropertyChanged(nameof(HasActiveFilters));
    }

    private string CreateWhenFilterLabel()
    {
        if (!IsCustomRange)
        {
            return $"When: {SelectedWhen?.DisplayName}";
        }
        var from = FromDate?.ToString("d") ?? "open";
        var to = ToDate?.ToString("d") ?? "open";
        return $"When: {from} to {to}";
    }

    private HistorySearchRequest CreateRequest(string? continuation)
    {
        var role = Enum.TryParse<HistorySearchRoleFilter>(SelectedRole?.Id, out var parsedRole)
            ? parsedRole
            : HistorySearchRoleFilter.Any;
        var activity = Enum.TryParse<HistoryActivityKind>(SelectedActivity?.Id, out var parsedActivity)
            ? parsedActivity
            : HistoryActivityKind.None;
        var sessionId = Guid.TryParse(SelectedSession?.Id, out var parsedSessionId)
            ? parsedSessionId
            : (Guid?)null;
        var workspaceId = SelectedScope?.Id switch
        {
            AllWorkspacesScopeId => null,
            SpecificWorkspaceScopeId => ResolveSpecificWorkspaceId(),
            _ => _currentWorkspaceId,
        };
        var (fromUtc, toUtc) = _dateRangeSnapshot ??= ResolveDateRange();
        return new HistorySearchRequest(
            QueryText,
            workspaceId,
            sessionId,
            NormalizeFilterId(SelectedProfile?.Id),
            role,
            activity,
            fromUtc,
            toUtc,
            sessionId is null || IncludeChildSessions,
            HistorySearchLimits.DefaultResults,
            continuation);
    }

    private (DateTimeOffset? FromUtc, DateTimeOffset? ToUtc) ResolveDateRange()
    {
        var now = _timeProvider.GetUtcNow();
        return SelectedWhen?.Id switch
        {
            TodayId => (GetLocalDayStartUtc(now), now),
            PastSevenDaysId => (now.AddDays(-7), now),
            PastThirtyDaysId => (now.AddDays(-30), now),
            PastYearId => (now.AddYears(-1), now),
            CustomRangeId => ResolveCustomDateRange(),
            _ => (null, null),
        };
    }

    private (DateTimeOffset? FromUtc, DateTimeOffset? ToUtc) ResolveCustomDateRange()
    {
        var first = FromDate?.Date;
        var second = ToDate?.Date;
        var fromDate = first is not null && second is not null && first > second ? second : first;
        var toDate = first is not null && second is not null && first > second ? first : second;
        return (
            fromDate is { } from ? GetLocalBoundaryUtc(from) : null,
            toDate is { } to ? GetLocalBoundaryUtc(to.AddDays(1)).AddTicks(-1) : null);
    }

    private DateTimeOffset GetLocalDayStartUtc(DateTimeOffset utcNow)
    {
        var local = TimeZoneInfo.ConvertTime(utcNow, _timeProvider.LocalTimeZone);
        return GetLocalBoundaryUtc(local.Date);
    }

    private DateTimeOffset GetLocalBoundaryUtc(DateTime localDate)
        => ResolveLocalBoundaryUtc(localDate, _timeProvider.LocalTimeZone);

    internal static DateTimeOffset ResolveLocalBoundaryUtc(DateTime localDate, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        for (var minute = 0; minute < 2 * 24 * 60 && timeZone.IsInvalidTime(local); minute++)
        {
            local = local.AddMinutes(1);
        }
        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static string? NormalizeFilterId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private string ResolveSpecificWorkspaceId()
    {
        var selectedId = NormalizeFilterId(SelectedWorkspace?.Id);
        return selectedId
               ?? NormalizeFilterId(_currentWorkspaceId)
               ?? MissingWorkspaceScopeId;
    }

    private void ReconcileCurrentWorkspaceSessionFilter()
    {
        if (SelectedScope?.Id != CurrentWorkspaceScopeId
            || !Guid.TryParse(SelectedSession?.Id, out _)
            || _currentWorkspaceId is not null
            && string.Equals(
                SelectedSession?.ParentId,
                _currentWorkspaceId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _suppressSelectionChanges = true;
        try
        {
            RebuildSessionOptions(selectedSessionId: null);
            IncludeChildSessions = true;
        }
        finally
        {
            _suppressSelectionChanges = false;
        }
        NotifyFilterShapeChanged();
    }
}
