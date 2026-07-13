using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentProfilesViewModel
{
    private async Task LoadSelectedProfileAsync(AgentProfileRecord? profile, int version)
    {
        if (profile is null)
        {
            ClearEditor();
            EndHydration(version);
            return;
        }

        BeginHydration(version);
        var startEditRevision = _editRevision;
        try
        {
            var hasDraft = _drafts.TryGetValue(profile.ProfileId, out var document);
            var preserveDirtyDraft = hasDraft && document!.IsDirty;
            var draft = preserveDirtyDraft ? document!.Value : CreatePersistedDraft(profile);
            if (!preserveDirtyDraft)
            {
                document = new EditableDocumentState<ProfileEditorDraft>(draft, DraftComparer);
                _drafts[profile.ProfileId] = document;
            }

            var localToolsTask = _profileService.ListInstalledLocalToolsAsync(_lifetimeCancellation.Token);
            var packageCapabilitiesTask = _profileService.ListSelectableProfileCapabilitiesAsync(
                BuildCapabilityRequestProfile(profile, draft),
                _lifetimeCancellation.Token);
            var behaviorLoops = _profileService.ListBehaviorLoopDescriptors()
                .Select(loop => new BehaviorLoopOption(
                    loop.LoopId,
                    loop.SourceId,
                    loop.DisplayName,
                    loop.Description))
                .ToArray();

            _suppressDraftTracking = true;
            try
            {
                DisplayName = draft.DisplayName;
                Description = draft.Description;
                Instructions = draft.Instructions;
                HasEmbeddingConsumers = _profileService.HasProfileCapabilityConsumers(
                    AgentModelCapabilityKinds.Embedding);
            }
            finally
            {
                _suppressDraftTracking = false;
            }

            await Task.WhenAll(
                ChatBinding.RefreshAsync(draft.ChatBinding, _lifetimeCancellation.Token),
                EmbeddingBinding.RefreshAsync(draft.EmbeddingBinding, _lifetimeCancellation.Token),
                localToolsTask,
                packageCapabilitiesTask).ConfigureAwait(false);
            var localTools = await localToolsTask.ConfigureAwait(false);
            var packageCapabilities = await packageCapabilitiesTask.ConfigureAwait(false);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentProfileLoad(version, profile.ProfileId))
                {
                    return;
                }

                _suppressDraftTracking = true;
                try
                {
                    ApplyBehaviorLoopSelection(behaviorLoops, draft.BehaviorLoopId, draft.BehaviorLoopSourceId);
                    ApplyCapabilityOptions(
                        localTools,
                        packageCapabilities,
                        draft.CapabilityAssignments,
                        preserveCurrent: false);
                }
                finally
                {
                    _suppressDraftTracking = false;
                }

                var current = CaptureDraft();
                if (!preserveDirtyDraft && _editRevision == startEditRevision)
                {
                    _drafts[profile.ProfileId] = new EditableDocumentState<ProfileEditorDraft>(current, DraftComparer);
                }
                else
                {
                    document!.Value = current;
                }

                OnPropertyChanged(nameof(IsDirty));
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed && IsCurrentProfileLoad(version, profile.ProfileId))
                {
                    SetStatus(ex.Message, AgentProfileStatusKind.Error);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await _uiDispatcher.InvokeAsync(() => EndHydration(version)).ConfigureAwait(false);
        }
    }

    private async Task RefreshSelectedProfileCapabilitiesAsync()
    {
        var profile = SelectedProfile;
        if (profile is null)
        {
            return;
        }

        var version = _profileLoadVersion;
        var operation = BeginOperation(AgentProfileOperation.RefreshCapabilities);
        try
        {
            var requestProfile = BuildCapabilityRequestProfile(profile, CaptureDraft());
            var localToolsTask = _profileService.ListInstalledLocalToolsAsync(_lifetimeCancellation.Token);
            var packageCapabilitiesTask = _profileService.ListSelectableProfileCapabilitiesAsync(
                requestProfile,
                _lifetimeCancellation.Token);
            await Task.WhenAll(localToolsTask, packageCapabilitiesTask).ConfigureAwait(false);
            var localTools = await localToolsTask.ConfigureAwait(false);
            var packageCapabilities = await packageCapabilitiesTask.ConfigureAwait(false);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentProfileLoad(version, profile.ProfileId))
                {
                    return;
                }

                _suppressDraftTracking = true;
                try
                {
                    ApplyCapabilityOptions(
                        localTools,
                        packageCapabilities,
                        Capabilities.Assignments,
                        preserveCurrent: true);
                }
                finally
                {
                    _suppressDraftTracking = false;
                }

                UpdateCurrentDraft();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed && IsCurrentProfileLoad(version, profile.ProfileId))
                {
                    SetStatus(ex.Message, AgentProfileStatusKind.Error);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    EndOperation(operation);
                }
            }).ConfigureAwait(false);
        }
    }

    private void ApplyCapabilityOptions(
        IReadOnlyList<AgentToolCatalogEntry> localTools,
        IReadOnlyList<AgentProfileSelectableCapabilityDescriptor> packageCapabilities,
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> assignments,
        bool preserveCurrent)
    {
        var definitions = localTools.Select(item =>
            {
                var descriptor = item.Descriptor;
                var aliases = descriptor.Aliases?
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Select(alias => new AgentProfileSelectableCapabilityAssignmentRecord(
                        AgentProfileSelectableCapabilityKinds.Tool,
                        alias,
                        descriptor.SourceId))
                    .ToArray();
                return new CapabilityOptionDefinition(
                    "local",
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
            .Concat(packageCapabilities.Select(capability => new CapabilityOptionDefinition(
                "package",
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

        Replace(LocalTools, Capabilities.GetOptions("local"));
        Replace(PackageCapabilities, Capabilities.GetOptions("package"));
        Replace(LocalToolGroups, Capabilities.GetGroups("local"));
        Replace(PackageCapabilityGroups, Capabilities.GetGroups("package"));
        Replace(CapabilityGroups, Capabilities.Groups);
        RefreshCapabilitySummaries();
        OnPropertyChanged(nameof(HasLocalTools));
        OnPropertyChanged(nameof(HasPackageCapabilities));
        OnPropertyChanged(nameof(HasToolCallingConfiguration));
    }

    private void ApplyBehaviorLoopSelection(
        IReadOnlyList<BehaviorLoopOption> availableLoops,
        string? selectedLoopId,
        string? selectedSourceId)
    {
        Replace(BehaviorLoops, availableLoops);
        SelectedBehaviorLoop = BehaviorLoops.FirstOrDefault(option =>
                !string.IsNullOrWhiteSpace(selectedLoopId)
                && string.Equals(option.LoopId, selectedLoopId, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(selectedSourceId)
                    || string.Equals(option.SourceId, selectedSourceId, StringComparison.OrdinalIgnoreCase)))
            ?? BehaviorLoops.FirstOrDefault(option => string.Equals(
                option.LoopId,
                "default",
                StringComparison.OrdinalIgnoreCase))
            ?? BehaviorLoops.FirstOrDefault();
    }

    private void RefreshCapabilitySummaries()
    {
        var enabledTools = LocalTools.Where(option => option.IsEnabled).Select(option => option.DisplayName).ToArray();
        ToolSelectionSummary = enabledTools.Length == 0
            ? "No local tools are enabled for this profile."
            : $"Enabled local tools: {string.Join(", ", enabledTools)}";
        var enabledPackages = PackageCapabilities.Where(option => option.IsEnabled).Select(option => option.DisplayName).ToArray();
        PackageCapabilitySelectionSummary = enabledPackages.Length == 0
            ? "No package capabilities are enabled for this profile."
            : $"Enabled package capabilities: {string.Join(", ", enabledPackages)}";
    }

    private ProfileEditorDraft CreatePersistedDraft(AgentProfileRecord profile)
    {
        var chatBinding = FindModelBinding(profile, AgentModelCapabilityKinds.Chat);
        var embeddingBinding = FindModelBinding(profile, AgentModelCapabilityKinds.Embedding);
        return new ProfileEditorDraft(
            profile.DisplayName,
            profile.Description ?? string.Empty,
            profile.Instructions ?? string.Empty,
            new ModelBindingSelection(chatBinding?.ProviderId, chatBinding?.ModelId, chatBinding?.SettingsJson),
            new ModelBindingSelection(embeddingBinding?.ProviderId, embeddingBinding?.ModelId, embeddingBinding?.SettingsJson),
            profile.BehaviorLoopId,
            profile.BehaviorLoopSourceId,
            profile.SelectableCapabilityAssignments ?? []);
    }

    private ProfileEditorDraft CaptureDraft() => new(
        DisplayName,
        Description,
        Instructions,
        ChatBinding.Selection,
        EmbeddingBinding.Selection,
        SelectedBehaviorLoop?.LoopId,
        SelectedBehaviorLoop?.SourceId,
        Capabilities.Assignments);

    private void UpdateCurrentDraft()
    {
        if (_suppressDraftTracking || SelectedProfile is null
            || !_drafts.TryGetValue(SelectedProfile.ProfileId, out var document))
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

        _editRevision++;
        UpdateCurrentDraft();
    }

    private static AgentProfileRecord BuildCapabilityRequestProfile(
        AgentProfileRecord profile,
        ProfileEditorDraft draft)
        => profile with
        {
            SelectableCapabilityAssignments = draft.CapabilityAssignments,
            BehaviorLoopId = draft.BehaviorLoopId ?? profile.BehaviorLoopId,
            BehaviorLoopSourceId = draft.BehaviorLoopSourceId ?? profile.BehaviorLoopSourceId,
        };
}
