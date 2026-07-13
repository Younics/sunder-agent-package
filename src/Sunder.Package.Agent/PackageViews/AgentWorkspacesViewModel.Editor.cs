using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentWorkspacesViewModel
{
    public async Task ExecuteEditorActionAsync(AgentEditorActionViewModel action)
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
                case AgentEditorActionKind.RefreshEditor:
                    await RefreshEditorSectionsAsync();
                    SetStatus("Workspace editor refreshed.", AgentWorkspaceStatusKind.Success, autoClear: true);
                    break;
                case AgentEditorActionKind.RefreshField:
                    await RefreshEditorFieldAsync(action.Field);
                    SetStatus("Workspace editor field refreshed.", AgentWorkspaceStatusKind.Success, autoClear: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
        }
    }

    private void ReloadTargets(string? preferredTargetId = null)
    {
        ExecutionTargets.Clear();
        ExecutionTargets.Add(ExecutionTargetOption.Unconfigured);
        foreach (var target in _executionGateway.ListTargets())
        {
            ExecutionTargets.Add(new ExecutionTargetOption(
                target.TargetId,
                target.DisplayName,
                target.Description ?? target.TargetId));
        }

        if (preferredTargetId is not null)
        {
            SetSelectionSilently(() => SelectedExecutionTarget = ResolveTargetOption(preferredTargetId));
        }

        OnPropertyChanged(nameof(HasExecutionTargetChoices));
        OnPropertyChanged(nameof(HasNoExecutionTargetChoices));
    }

    private void OnExtensionCatalogChanged(object? sender, EventArgs e)
        => RunOnUiThread(ApplyExtensionCatalogChanges);

    private void OnExtensionCatalogChanged(object? sender, PackageExtensionCatalogChangedEventArgs e)
    {
        if (e.IncludesExtensionPoint(PackageExtensionPoints.ExecutionTargets.Id)
            || e.IncludesExtensionPoint(PackageExtensionPoints.WorkspaceEditorContributors.Id))
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
        TrackOperation(RefreshEditorSectionsAsync());
    }

    private async Task RefreshEditorSectionsAsync()
    {
        var context = BuildEditorContext();
        if (context is null)
        {
            EditorSections.Clear();
            return;
        }

        var operation = BeginOperation(AgentWorkspaceOperation.DiscoverEditor);
        try
        {
            var editorSections = new List<AgentEditorSectionViewModel>();
            var contributors = _extensionCatalog.GetExtensions(PackageExtensionPoints.WorkspaceEditorContributors)
                .Where(contributor => contributor.CanEdit(context))
                .ToArray();
            foreach (var contributor in contributors)
            {
                var sections = await contributor.GetSectionsAsync(context);
                foreach (var section in sections)
                {
                    editorSections.Add(new AgentEditorSectionViewModel(contributor, context, section));
                }
            }

            if (!_operation.IsCurrent(operation))
            {
                return;
            }

            EditorSections.Clear();
            foreach (var section in editorSections)
            {
                EditorSections.Add(section);
            }
            ClearStatus();
        }
        catch (Exception ex)
        {
            if (_operation.IsCurrent(operation))
            {
                SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
            }
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private async Task RefreshEditorFieldAsync(AgentEditorFieldViewModel field)
    {
        var context = BuildEditorContext();
        if (context is null || !field.Section.Contributor.CanEdit(context))
        {
            return;
        }

        var sections = await field.Section.Contributor.GetSectionsAsync(context);
        var refreshedSection = sections.FirstOrDefault(section => string.Equals(section.SectionId, field.Section.SectionId, StringComparison.OrdinalIgnoreCase));
        var refreshedField = refreshedSection?.Fields.FirstOrDefault(candidate => string.Equals(candidate.FieldId, field.FieldId, StringComparison.OrdinalIgnoreCase));
        if (refreshedField is null)
        {
            await RefreshEditorSectionsAsync();
            return;
        }

        field.ApplyField(refreshedField);
    }

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

    private static async Task<AgentEditorSaveResult> SaveEditorSectionsAsync(
        IReadOnlyList<AgentEditorSectionViewModel> sections)
    {
        foreach (var section in sections)
        {
            var result = await section.SaveAsync();
            if (!result.Success)
            {
                return result;
            }
        }

        return AgentEditorSaveResult.Ok("Workspace editor sections saved.");
    }

    private ExecutionTargetOption ResolveTargetOption(string? contributionId)
        => ExecutionTargets.FirstOrDefault(target => string.Equals(target.TargetId, contributionId, StringComparison.OrdinalIgnoreCase))
           ?? ExecutionTargetOption.Unconfigured;
}
