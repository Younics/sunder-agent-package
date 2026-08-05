using System.ComponentModel;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.Services;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubagentsViewModel
{
    private void ApplyCapabilityOptions(
        IReadOnlyList<AgentToolDescriptor> localTools,
        IReadOnlyList<AgentProfileSelectableCapabilityDescriptor> packageCapabilities,
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> assignments,
        bool preserveCurrent)
    {
        var definitions = localTools.Select(descriptor =>
            {
                var aliases = descriptor.Aliases?
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Select(alias => new AgentProfileSelectableCapabilityAssignmentRecord(
                        AgentProfileSelectableCapabilityKinds.Tool,
                        alias,
                        descriptor.SourceId))
                    .ToArray();
                return new CapabilityOptionDefinition(
                    "capability",
                    AgentProfileSelectableCapabilityKinds.Tool,
                    descriptor.ToolId,
                    descriptor.SourceId,
                    descriptor.DisplayName,
                    descriptor.Description,
                    string.Empty,
                    CanSelect: true,
                    CapabilityGrouping.ForTool(descriptor),
                    aliases,
                    AllowUnscopedAssignment: true);
            })
            .Concat(packageCapabilities
                .Where(capability => !string.Equals(
                        capability.Kind,
                        AgentProfileSelectableCapabilityKinds.Subagent,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(capability.SourceId, SubagentConstants.PackageId, StringComparison.OrdinalIgnoreCase))
                .Select(capability => new CapabilityOptionDefinition(
                    "capability",
                    capability.Kind,
                    capability.CapabilityId,
                    capability.SourceId,
                    capability.DisplayName,
                    capability.Description,
                    capability.StatusText ?? string.Empty,
                    capability.IsSelectable,
                    CapabilityGrouping.ForPackage(capability))))
            .ToArray();
        if (preserveCurrent)
        {
            Capabilities.Reconcile(definitions);
        }
        else
        {
            Capabilities.Load(definitions, assignments);
        }
    }

    private SubagentEditorDraft CreatePersistedDraft(SubagentRecord subagent) => new(
        subagent.DisplayName,
        subagent.Description ?? string.Empty,
        subagent.Instructions ?? string.Empty,
        new ModelBindingSelection(subagent.ChatProviderId, subagent.ChatModelId, subagent.ChatModelSettingsJson),
        subagent.SelectableCapabilityAssignments ?? []);

    private SubagentEditorDraft CaptureDraft() => new(
        DisplayName,
        Description,
        Instructions,
        ChatBinding.Selection,
        Capabilities.Assignments);

    private void UpdateCurrentDraft()
    {
        if (_suppressDraftTracking || SelectedSubagent is null
            || !_drafts.TryGetValue(SelectedSubagent.SubagentId, out var document))
        {
            return;
        }

        var wasDirty = document.IsDirty;
        document.Value = CaptureDraft();
        if (wasDirty != document.IsDirty)
        {
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    private void OnEditorChanged()
    {
        if (_suppressDraftTracking)
        {
            return;
        }

        _listDetail.PromoteSelectionToExplicit();
        _editRevision++;
        UpdateCurrentDraft();
    }

    private void Subscribe()
    {
        ChatBinding.PropertyChanged += OnModelBindingPropertyChanged;
        ChatBinding.Changed += OnEditorSelectionChanged;
        Capabilities.Changed += OnCapabilitiesChanged;
        if (_gateway is not null)
        {
            _gateway.SubagentsChanged += OnSubagentsChanged;
            _gateway.CatalogChanged += OnSelectableCapabilitiesChanged;
        }
    }

    private void OnModelBindingPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        OnPropertyChanged(string.Empty);
        SaveSubagentCommand.NotifyCanExecuteChanged();
        DeleteSubagentCommand.NotifyCanExecuteChanged();
    }

    private void OnEditorSelectionChanged() => OnEditorChanged();

    private void OnCapabilitiesChanged() => OnEditorChanged();

    private void OnSelectableCapabilitiesChanged()
        => _tasks.Run(_ => RefreshSelectedSubagentCapabilitiesAsync());

    private void OnSubagentsChanged() => _tasks.Run(_runtimeRefresh.MarkDirty());
}
