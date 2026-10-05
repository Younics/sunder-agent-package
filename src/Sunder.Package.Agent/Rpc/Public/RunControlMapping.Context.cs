using Domain = Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Rpc.Public.Control;

internal static partial class RunControlMapping
{
    internal static Domain.AgentBehaviorLoopContext ToDomain(AgentBehaviorLoopContext value)
        => new(ToDomain(value.Session), ToDomain(value.Profile), value.ProviderId.Value, value.ModelId.Value, ToDomain(value.RunCapabilities),
            Reference(value.Workspace, ToDomain), Reference(value.ExecutionBinding, ToDomain), value.RunId.Value, value.RunRevision.Value,
            ToDomain(value.RunningCheckpoint), value.RunStartedAtUtc.ToDomain(), value.UserMessage.Value, value.UserTurnId.Value,
            Reference(value.ModelVariant, item => new Domain.AgentModelVariantDescriptor(item.VariantId.Value, item.DisplayName.Value,
                Reference(item.Description, text => text.Value), Nullable(item.ReasoningEffort, effort => Enum.Parse<Domain.AgentReasoningEffort>(effort.ToString())))),
            Reference(value.ModelSpeedOption, item => new Domain.AgentModelSpeedOptionDescriptor(item.SpeedOptionId.Value, item.DisplayName.Value,
                Reference(item.Description, text => text.Value))),
            Reference(value.ModelModeOption, item => new Domain.AgentModelModeOptionDescriptor(item.ModeOptionId.Value, item.DisplayName.Value,
                Reference(item.Description, text => text.Value), item.DisablesReasoning.Value)));

    private static Domain.AgentSessionRecord ToDomain(AgentSessionRecord value)
        => new(value.SessionId.Value, value.Title.Value, Enum.Parse<Domain.AgentSessionState>(value.State.ToString()),
            value.CreatedAtUtc.ToDomain(), value.UpdatedAtUtc.ToDomain(), Nullable(value.ParentSessionId, item => item.Value),
            Nullable(value.RootSessionId, item => item.Value), Nullable(value.ParentRunId, item => item.Value), Nullable(value.ParentRunRevision, item => item.Value),
            Reference(value.ParentToolCallId, item => item.Value), Reference(value.TaskId, item => item.Value), Reference(value.ProfileId, item => item.Value),
            Reference(value.BehaviorLoopId, item => item.Value), Reference(value.AgentKind, item => item.Value), Reference(value.WorkspaceId, item => item.Value));

    internal static Domain.AgentProfileRecord ToDomain(AgentProfileRecord value)
        => new(value.ProfileId.Value, value.DisplayName.Value, Reference(value.Description, item => item.Value), Reference(value.Instructions, item => item.Value),
            Reference(value.ChatProviderId, item => item.Value), Reference(value.ChatModelId, item => item.Value),
            Reference(value.EmbeddingProviderId, item => item.Value), Reference(value.EmbeddingModelId, item => item.Value),
            value.CreatedAtUtc.ToDomain(), value.UpdatedAtUtc.ToDomain(),
            Reference(value.ModelBindings, items => items.Value.Select(item => new Domain.AgentProfileModelBindingRecord(
                item.ProfileId.Value, item.CapabilityKind.Value, Reference(item.ProviderId, text => text.Value), Reference(item.ModelId, text => text.Value),
                Reference(item.SettingsJson, text => text.Value), item.UpdatedAtUtc.ToDomain())).ToArray()),
            Reference(value.SelectableCapabilityAssignments, items => items.Value.Select(item => new Domain.AgentProfileSelectableCapabilityAssignmentRecord(
                item.Kind.Value, item.CapabilityId.Value, Reference(item.SourceId, text => text.Value))).ToArray()),
            Reference(value.BehaviorLoopId, item => item.Value), Reference(value.BehaviorLoopSourceId, item => item.Value),
            Reference(value.BehaviorLoopSettingsJson, item => item.Value), value.IsInternal.Value);

    internal static Domain.AgentWorkspaceRecord ToDomain(AgentWorkspaceRecord value)
        => new(value.WorkspaceId.Value, value.DisplayName.Value, Reference(value.Description, item => item.Value), value.CreatedAtUtc.ToDomain(), value.UpdatedAtUtc.ToDomain(),
            value.Paths.Value.Select(item => new Domain.AgentWorkspacePathRecord(item.PathId.Value, item.WorkspaceId.Value, item.HostPath.Value,
                item.IsDefault.Value, checked((int)item.SortOrder.Value), item.CreatedAtUtc.ToDomain(), item.UpdatedAtUtc.ToDomain())).ToArray(),
            value.Documents.Value.Select(item => new Domain.AgentWorkspaceDocumentRecord(item.DocumentId.Value, item.WorkspaceId.Value, item.FilePath.Value,
                checked((int)item.SortOrder.Value), item.CreatedAtUtc.ToDomain(), item.UpdatedAtUtc.ToDomain())).ToArray());

    internal static Domain.AgentWorkspaceBindingRecord ToDomain(AgentWorkspaceBindingRecord value)
        => new(value.BindingId.Value, value.WorkspaceId.Value, value.ExtensionPointId.Value, value.ContributionId.Value, value.Role.Value,
            value.IsEnabled.Value, checked((int)value.SortOrder.Value), value.CreatedAtUtc.ToDomain(), value.UpdatedAtUtc.ToDomain());

    private static Domain.AgentProviderRunCapabilities ToDomain(AgentProviderRunCapabilities value)
        => new(value.SupportsNativeToolCalling.Value, value.SupportsStreamingToolCalls.Value, value.SupportsMultipleToolCalls.Value, value.Summary.Value,
            value.SupportsImageInput.Value, value.SupportsPdfInput.Value, value.SupportsAudioInput.Value, value.SupportsVideoInput.Value,
            Nullable(value.ContextWindowTokens, item => checked((int)item.Value)), Nullable(value.MaxOutputTokens, item => checked((int)item.Value)));
}
