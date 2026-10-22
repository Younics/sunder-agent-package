using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.PackageViews;

internal sealed record AgentWorkspaceEditorIntent(
    long Revision,
    long EditorRevision,
    string WorkspaceId,
    string TargetId,
    AdaptiveListDetailLayout Layout);

internal sealed record AgentExecutionTargetRefreshRequest(
    LatestRequestTicket Ticket,
    long SelectionRevision,
    string? PreferredTargetId);

public sealed partial class AgentWorkspacesViewModel
{
    public async Task ExecuteEditorActionAsync(
        AgentEditorActionViewModel action,
        CancellationToken cancellationToken = default)
    {
        try
        {
            switch (action.Kind)
            {
                case AgentEditorActionKind.OpenPackageSettings:
                    if (_settingsNavigationService is null || string.IsNullOrWhiteSpace(action.PackageId))
                    {
                        SetStatus("Package settings cannot be opened from this host.", AgentWorkspaceStatusKind.Warning);
                        return;
                    }

                    var opened = await _settingsNavigationService.OpenPackageSettingsAsync(
                        action.PackageId,
                        action.Parameters);
                    SetStatus(
                        opened ? "Opened package settings." : "Package settings could not be opened.",
                        opened ? AgentWorkspaceStatusKind.Success : AgentWorkspaceStatusKind.Warning,
                        autoClear: opened);
                    break;
                case AgentEditorActionKind.RefreshField:
                    if (await RefreshEditorFieldAsync(action.Field, cancellationToken))
                    {
                        SetStatus("Workspace editor field refreshed.", AgentWorkspaceStatusKind.Success, autoClear: true);
                    }
                    break;
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || _lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
        }
    }

    private void ReloadTargets(
        IReadOnlyList<AgentExecutionTargetDescriptor> targets,
        string? preferredTargetId = null,
        bool restoreSelection = false)
    {
        var wasSuppressed = _suppressDraftTracking;
        if (restoreSelection)
        {
            _suppressDraftTracking = true;
        }

        try
        {
            ExecutionTargets.Clear();
            ExecutionTargets.Add(ExecutionTargetOption.Unconfigured);
            foreach (var target in targets)
            {
                ExecutionTargets.Add(new ExecutionTargetOption(
                    target.TargetId,
                    target.DisplayName,
                    target.Description ?? target.TargetId));
            }

            if (restoreSelection)
            {
                SelectedExecutionTarget = ResolveTargetOption(preferredTargetId);
            }
        }
        finally
        {
            _suppressDraftTracking = wasSuppressed;
        }

        OnPropertyChanged(nameof(HasExecutionTargetChoices));
        OnPropertyChanged(nameof(HasNoExecutionTargetChoices));
    }

    private Task StartExecutionTargetRefresh()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        _currentExecutionTargetRefresh = _tasks.Track(RefreshExecutionTargetsAsync);
        return _currentExecutionTargetRefresh;
    }

    private async Task RefreshExecutionTargetsAsync(CancellationToken cancellationToken)
    {
        AgentExecutionTargetRefreshRequest? request = null;
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            var preferredTargetId = SelectedExecutionTarget?.TargetId;
            if (SelectedExecutionTarget is null && SelectedWorkspace is not null)
            {
                preferredTargetId = ResolveWorkspaceTargetId(SelectedWorkspace.WorkspaceId);
            }

            request = new AgentExecutionTargetRefreshRequest(
                _requests.Begin(ExecutionTargetRefreshChannel, cancellationToken),
                _executionTargetSelectionRevision,
                preferredTargetId);
        }).ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        try
        {
            var targets = await _executionGateway.ListTargetsAsync(request.Ticket.CancellationToken).ConfigureAwait(false);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_disposed || !_requests.IsCurrent(request.Ticket))
                {
                    return;
                }

                var selectionChanged = request.SelectionRevision != _executionTargetSelectionRevision;
                var preferredTargetId = selectionChanged
                    ? SelectedExecutionTarget?.TargetId
                    : request.PreferredTargetId;
                if (selectionChanged
                    && preferredTargetId is not null
                    && !targets.Any(target => string.Equals(
                        target.TargetId,
                        preferredTargetId,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                ReloadTargets(targets, preferredTargetId, restoreSelection: true);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            request.Ticket.CancellationToken.IsCancellationRequested
            && exception.CancellationToken == request.Ticket.CancellationToken)
        {
            // This exact request token is linked to the owner and is also
            // cancelled on supersession. Neither path may escape as a foreign
            // cancellation; unrelated/tokenless cancellations still propagate.
        }
        finally
        {
            _requests.Complete(request.Ticket);
        }
    }

    private async Task ObserveEditorProvidersAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await _editors.ListAsync(cancellationToken).ConfigureAwait(false);
                await foreach (var change in _editors.WatchAsync(snapshot.Revision, snapshot.Sequence, cancellationToken).ConfigureAwait(false))
                {
                    OnEditorCatalogChanged();
                    if (change.Kind == ServiceProviderChangeKind.ResetRequired) break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (!PresentationExceptionPolicy.IsFatal(exception)) { OnEditorCatalogChanged(); }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnEditorCatalogChanged()
        => RunOnUiThread(() =>
        {
            if (_disposed) return;
            if (!_isInitialized) { _initializationCatalogRefreshPending = true; return; }
            ApplyExtensionCatalogChanges();
        });

    private void ApplyExtensionCatalogChanges()
    {
        if (_disposed)
        {
            return;
        }

        var targetRefresh = StartExecutionTargetRefresh();
        _tasks.Run(async cancellationToken =>
        {
            try
            {
                await targetRefresh.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed && ReferenceEquals(targetRefresh, _currentExecutionTargetRefresh))
                {
                    StartEditorSectionRefresh();
                }
            }).ConfigureAwait(false);
        });
    }

    private void StartEditorSectionRefresh()
    {
        if (!_listDetail.IsExistingDetail)
        {
            ReplaceEditorSections([]);
            _currentEditorSectionRefresh = Task.CompletedTask;
            return;
        }

        var ticket = _listDetail.BeginDetailLoad(_lifetimeCancellation.Token);
        _currentEditorSectionRefresh = _tasks.Track(_ => RefreshEditorSectionsAsync(ticket));
    }

    private async Task RefreshEditorSectionsAsync(AdaptiveDetailTicket<string> ticket)
    {
        var context = BuildEditorContext();
        if (context is null)
        {
            if (_listDetail.IsCurrentDetail(ticket))
            {
                ReplaceEditorSections([]);
                CaptureCurrentWorkspaceDraft();
                _listDetail.TrySetDetailReady(ticket);
            }
            return;
        }

        var operation = BeginOperation(AgentWorkspaceOperation.DiscoverEditor);
        try
        {
            var editorSections = new List<AgentEditorSectionViewModel>();
            var intent = CaptureEditorIntent(context, ticket.IntentRevision);
            var failureCount = 0;
            var catalog = await _editors.ListAsync(ticket.Request.CancellationToken);
            foreach (var contributorReference in catalog.Providers.Where(provider => provider.State == ServiceProviderState.Active))
            {
                var result = await AgentEditorSectionViewModel.TryGetApplicableSectionsAsync(
                    contributorReference,
                    context,
                    AgentEditorInvocationOperation.Discovery,
                    ticket.Request.CancellationToken);
                if (!result.Success)
                {
                    failureCount++;
                    editorSections.Add(CreateEditorErrorSection(
                        contributorReference,
                        context,
                        result.Failure!,
                        new AgentEditorRetryState(AgentEditorRetryKind.Discovery, intent)));
                    continue;
                }

                foreach (var section in result.Result)
                {
                    editorSections.Add(new AgentEditorSectionViewModel(
                        contributorReference,
                        context,
                        section));
                }
            }

            if (!_operation.IsCurrent(operation) || !_listDetail.IsCurrentDetail(ticket))
            {
                return;
            }

            _suppressDraftTracking = true;
            try
            {
                ReplaceEditorSections(editorSections);
            }
            finally
            {
                _suppressDraftTracking = false;
            }
            if (SelectedWorkspace is { } workspace)
            {
                if (_workspaceDrafts.TryGetValue(workspace.WorkspaceId, out var draft) && draft.IsDirty)
                {
                    draft.Draft = CaptureWorkspaceDraft(workspace);
                }
                else
                {
                    RegisterCleanWorkspaceDraft(workspace);
                }
            }
            _listDetail.TrySetDetailReady(ticket);
            if (failureCount == 0)
            {
                ClearStatus();
            }
            else
            {
                SetStatus(
                    failureCount == 1
                        ? "One execution-settings section is unavailable. Other sections remain available."
                        : $"{failureCount} execution-settings sections are unavailable. Other sections remain available.",
                    AgentWorkspaceStatusKind.Warning);
            }
        }
        catch (OperationCanceledException) when (ticket.Request.CancellationToken.IsCancellationRequested)
        {
            _listDetail.TryCancelDetailLoad(ticket);
        }
        catch (Exception ex)
        {
            if (_operation.IsCurrent(operation) && _listDetail.TrySetDetailError(ticket, ex))
            {
                SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
            }
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private async Task<bool> RefreshEditorFieldAsync(
        AgentEditorFieldViewModel field,
        CancellationToken cancellationToken)
    {
        var context = BuildEditorContext();
        if (context is null)
        {
            return false;
        }

        var intent = CaptureEditorIntent(context);
        var result = await field.Section.TryGetApplicableSectionsAsync(
            context,
            AgentEditorInvocationOperation.Refresh,
            cancellationToken);
        if (!IsCurrentEditorIntent(intent) || !EditorSections.Contains(field.Section))
        {
            return false;
        }
        if (!result.Success)
        {
            TryReplaceEditorSection(
                field.Section,
                [CreateEditorErrorSection(
                    field.Section.ContributorReference,
                    context,
                    result.Failure!,
                    new AgentEditorRetryState(
                        AgentEditorRetryKind.Refresh,
                        intent,
                        field.Section.SectionId,
                        field.Section))],
                intent);
            SetStatus(
                "One execution-settings section is unavailable. Other sections remain available.",
                AgentWorkspaceStatusKind.Warning);
            return false;
        }

        var refreshedSection = result.Result.FirstOrDefault(section =>
            string.Equals(section.SectionId, field.Section.SectionId, StringComparison.OrdinalIgnoreCase));
        if (refreshedSection is null)
        {
            TryReplaceEditorSection(field.Section, [], intent);
            return true;
        }

        var refreshedField = refreshedSection.Fields.FirstOrDefault(candidate =>
            string.Equals(candidate.FieldId, field.FieldId, StringComparison.OrdinalIgnoreCase));
        if (refreshedField is null)
        {
            TryReplaceEditorSection(
                field.Section,
                [new AgentEditorSectionViewModel(
                    field.Section.ContributorReference,
                    context,
                    refreshedSection)],
                intent);
            return true;
        }

        field.ApplyField(refreshedField);
        return true;
    }

    private AgentEditorSectionViewModel CreateEditorErrorSection(
        ServiceProvider<AgentServices.IWorkspaceEditorService> contributorReference,
        AgentWorkspaceEditorContext context,
        AgentEditorInvocationFailure failure,
        AgentEditorRetryState retryState)
        => AgentEditorSectionViewModel.CreateError(
            contributorReference,
            context,
            failure,
            retryState,
            StartEditorSectionRetry);

    private void StartEditorSectionRetry(AgentEditorSectionViewModel errorSection)
    {
        if (!errorSection.IsError
            || errorSection.IsRetrying
            || !EditorSections.Contains(errorSection)
            || !IsCurrentEditorIntent(errorSection.RetryState.Intent))
        {
            return;
        }

        errorSection.SetRetrying(true);
        _currentEditorSectionRetry = _tasks.Track(_ => RetryEditorSectionAsync(errorSection));
    }

    private async Task RetryEditorSectionAsync(AgentEditorSectionViewModel errorSection)
    {
        var operation = BeginOperation(AgentWorkspaceOperation.DiscoverEditor);
        try
        {
            var retry = errorSection.RetryState;
            if (retry.Kind == AgentEditorRetryKind.Save)
            {
                var original = retry.OriginalSection
                    ?? throw new InvalidOperationException("A save retry requires the original editor section.");
                var save = await original.TrySaveAsync(
                    _lifetimeCancellation.Token);
                if (!CanApplyEditorRetry(errorSection, retry.Intent))
                {
                    return;
                }
                if (!save.Success)
                {
                    ReplaceEditorRetryFailure(errorSection, save.Failure!);
                    return;
                }

                TryReplaceEditorSection(errorSection, [original], retry.Intent);
                SetStatus(
                    save.Result.Success ? "Workspace settings saved." : save.Result.Message,
                    save.Result.Success ? AgentWorkspaceStatusKind.Success : AgentWorkspaceStatusKind.Error,
                    autoClear: save.Result.Success);
                return;
            }

            var sections = await errorSection.TryGetApplicableSectionsAsync(
                errorSection.Context,
                retry.Kind == AgentEditorRetryKind.Discovery
                    ? AgentEditorInvocationOperation.Discovery
                    : AgentEditorInvocationOperation.Refresh,
                _lifetimeCancellation.Token);
            if (!CanApplyEditorRetry(errorSection, retry.Intent))
            {
                return;
            }
            if (!sections.Success)
            {
                ReplaceEditorRetryFailure(errorSection, sections.Failure!);
                return;
            }

            var replacements = sections.Result
                .Where(section => retry.Kind == AgentEditorRetryKind.Discovery
                                  || string.Equals(
                                      section.SectionId,
                                      retry.SectionId,
                                      StringComparison.OrdinalIgnoreCase))
                .Select(section => new AgentEditorSectionViewModel(
                    errorSection.ContributorReference,
                    errorSection.Context,
                    section))
                .ToArray();
            TryReplaceEditorSection(errorSection, replacements, retry.Intent);
            SetStatus("Execution settings loaded.", AgentWorkspaceStatusKind.Success, autoClear: true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (EditorSections.Contains(errorSection))
            {
                errorSection.SetRetrying(false);
            }
            EndOperation(operation);
        }
    }

    private void ReplaceEditorRetryFailure(
        AgentEditorSectionViewModel errorSection,
        AgentEditorInvocationFailure failure)
    {
        var retry = errorSection.RetryState;
        TryReplaceEditorSection(
            errorSection,
            [CreateEditorErrorSection(
                errorSection.ContributorReference,
                errorSection.Context,
                failure,
                retry)],
            retry.Intent);
        SetStatus(
            "One execution-settings section is unavailable. Other sections remain available.",
            AgentWorkspaceStatusKind.Warning);
    }

    private bool CanApplyEditorRetry(
        AgentEditorSectionViewModel errorSection,
        AgentWorkspaceEditorIntent intent)
        => EditorSections.Contains(errorSection) && IsCurrentEditorIntent(intent);

    private bool TryReplaceEditorSection(
        AgentEditorSectionViewModel current,
        IReadOnlyList<AgentEditorSectionViewModel> replacements,
        AgentWorkspaceEditorIntent intent)
    {
        if (!IsCurrentEditorIntent(intent))
        {
            return false;
        }

        var index = EditorSections.IndexOf(current);
        if (index < 0)
        {
            return false;
        }

        RemoveEditorSectionAt(index);
        foreach (var replacement in replacements)
        {
            InsertEditorSection(index++, replacement);
        }
        OnWorkspaceEditorChanged();
        return true;
    }

    private AgentWorkspaceEditorIntent CaptureEditorIntent(
        AgentWorkspaceEditorContext context,
        long? revision = null)
        => new(
            revision ?? IntentRevision,
            _editorIntentRevision,
            context.Workspace.WorkspaceId,
            context.TargetId,
            _listDetail.Layout);

    private bool IsCurrentEditorIntent(AgentWorkspaceEditorIntent intent)
        => !_disposed
           && _listDetail.IsExistingDetail
           && intent.Revision == IntentRevision
           && intent.EditorRevision == _editorIntentRevision
           && intent.Layout == _listDetail.Layout
           && string.Equals(
               intent.WorkspaceId,
               SelectedWorkspace?.WorkspaceId,
               StringComparison.OrdinalIgnoreCase)
           && string.Equals(
               intent.TargetId,
               SelectedExecutionTarget?.TargetId,
               StringComparison.OrdinalIgnoreCase);

    private AgentWorkspaceEditorContext? BuildEditorContext()
    {
        if (SelectedWorkspace is null || SelectedExecutionTarget is null || SelectedExecutionTarget.IsUnconfigured)
        {
            return null;
        }

        return new AgentWorkspaceEditorContext(
            SelectedWorkspace,
            SelectedExecutionTarget.TargetId!,
            AgentWorkspaceDefaults.BuildPrimaryBindingId(SelectedWorkspace.WorkspaceId));
    }

    private async Task<AgentEditorSaveResult> SaveEditorSectionsAsync(
        IReadOnlyList<AgentEditorSectionViewModel> sections,
        AgentWorkspaceEditorIntent? intent,
        CancellationToken cancellationToken)
    {
        AgentEditorSaveResult? firstFailure = null;
        foreach (var section in sections)
        {
            if (section.IsError)
            {
                firstFailure ??= AgentEditorSaveResult.Failed(
                    "Resolve unavailable execution-settings sections before saving the workspace.");
                continue;
            }

            var result = await section.TrySaveAsync(
                cancellationToken);
            if (!result.Success)
            {
                if (intent is not null && IsCurrentEditorIntent(intent))
                {
                    TryReplaceEditorSection(
                        section,
                        [CreateEditorErrorSection(
                            section.ContributorReference,
                            section.Context,
                            result.Failure!,
                            new AgentEditorRetryState(
                                AgentEditorRetryKind.Save,
                                intent,
                                section.SectionId,
                                section))],
                        intent);
                }
                firstFailure ??= AgentEditorSaveResult.Failed(
                    "Some execution settings could not be saved. Retry the affected section.");
                continue;
            }
            if (!result.Result.Success)
            {
                firstFailure ??= result.Result;
            }
        }

        return firstFailure ?? AgentEditorSaveResult.Ok("Workspace editor sections saved.");
    }

    private ExecutionTargetOption ResolveTargetOption(string? contributionId)
        => ExecutionTargets.FirstOrDefault(target => string.Equals(target.TargetId, contributionId, StringComparison.OrdinalIgnoreCase))
           ?? ExecutionTargetOption.Unconfigured;
}
