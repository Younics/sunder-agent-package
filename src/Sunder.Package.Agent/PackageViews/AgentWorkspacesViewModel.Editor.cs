using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.PackageViews;

internal sealed record AgentWorkspaceEditorIntent(
    long Revision,
    long EditorRevision,
    string WorkspaceId,
    string TargetId,
    AdaptiveListDetailLayout Layout);

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
        string? preferredTargetId = null)
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

        if (preferredTargetId is not null)
        {
            var wasSuppressed = _suppressDraftTracking;
            _suppressDraftTracking = true;
            try
            {
                SelectedExecutionTarget = ResolveTargetOption(preferredTargetId);
            }
            finally
            {
                _suppressDraftTracking = wasSuppressed;
            }
        }

        OnPropertyChanged(nameof(HasExecutionTargetChoices));
        OnPropertyChanged(nameof(HasNoExecutionTargetChoices));
    }

    private void ReloadTargets(string? preferredTargetId = null)
        => ReloadTargets(_executionGateway.ListTargets(), preferredTargetId);

    private void OnRpcCatalogChanged(object? sender, AgentRpcCatalogChangedEventArgs e)
    {
        if (_isInitialized
            && (e.IncludesContract(AgentRpcContractIds.ExecutionTarget)
                || e.IncludesContract(AgentRpcContractIds.WorkspaceEditor)))
        {
            RunOnUiThread(ApplyExtensionCatalogChanges);
        }
    }

    private void ApplyExtensionCatalogChanges()
    {
        if (_disposed)
        {
            return;
        }

        var preferredTargetId = SelectedExecutionTarget?.TargetId;
        if (string.IsNullOrWhiteSpace(preferredTargetId) && SelectedWorkspace is not null)
        {
            preferredTargetId = ResolveWorkspaceTargetId(SelectedWorkspace.WorkspaceId);
        }

        ReloadTargets(preferredTargetId);
        StartEditorSectionRefresh();
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
        _currentEditorSectionRefresh = RefreshEditorSectionsAsync(ticket);
        TrackOperation(_currentEditorSectionRefresh);
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
            foreach (var contributorReference in _rpcCatalog.GetServiceReferences(AgentRpcServices.WorkspaceEditors))
            {
                var result = await AgentEditorSectionViewModel.TryGetApplicableSectionsAsync(
                    contributorReference,
                    context,
                    _rpcCatalog,
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
            _rpcCatalog,
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
        AgentRpcReference<IAgentWorkspaceEditorContributor> contributorReference,
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
        _currentEditorSectionRetry = RetryEditorSectionAsync(errorSection);
        TrackOperation(_currentEditorSectionRetry);
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
                    _rpcCatalog,
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
                _rpcCatalog,
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
            AgentWorkspaceService.BuildPrimaryBindingId(SelectedWorkspace.WorkspaceId));
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
                _rpcCatalog,
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
