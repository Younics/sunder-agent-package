using Domain = Sunder.Package.Agent.Contracts.Models;
using Catalog = Sunder.Package.Agent.Rpc.Public.Catalog;

namespace Sunder.Package.Agent.Rpc.Public.Control;

internal static partial class RunControlMapping
{
    internal static Domain.AgentToolDescriptor ToDomain(AgentToolDescriptor value)
        => new(value.ToolId.Value, value.DisplayName.Value, value.Description.Value, value.IsReadOnly.Value, value.RequiresNetwork.Value,
            Reference(value.ArgumentsJsonSchema, item => item.Value), Reference(value.SourceKind, item => item.Value),
            Reference(value.SourceId, item => item.Value), Reference(value.SourceDisplayName, item => item.Value),
            Reference(value.Aliases, items => items.Value.Select(item => item.Value).ToArray()), Enum.Parse<Domain.AgentToolSelectionScope>(value.SelectionScope.ToString()),
            Reference(value.SelectionGroupId, item => item.Value), Reference(value.SelectionGroupDisplayName, item => item.Value),
            Reference(value.SelectionGroupDescription, item => item.Value), Reference(value.RuntimeInstructions, item => item.Value),
            Reference(value.ActivationRequirement, item => new Domain.AgentToolActivationRequirement(item.CapabilityKind.Value,
                SourceId: Reference(item.SourceId, text => text.Value), CapabilityId: Reference(item.CapabilityId, text => text.Value))),
            Enum.Parse<Domain.AgentToolPriority>(value.Priority.ToString()))
        { ConcurrencyMode = Enum.Parse<Domain.AgentToolConcurrencyMode>(value.ConcurrencyMode.ToString()) };

    internal static Domain.AgentToolCallRequest ToDomain(AgentToolCallRequest value)
        => new(value.CallId.Value, value.ToolId.Value, value.ArgumentsJson.Value);

    internal static AgentToolCallOutcome FromDomain(Domain.AgentToolCallOutcome value)
        => new()
        {
            Kind = Enum.Parse<AgentToolCallOutcomeKind>(value.Kind.ToString()),
            Checkpoint = FromReference(value.Checkpoint, item => Catalog.RuntimeCatalogMapping.RunCheckpoint(item)!),
            Result = FromReference(value.Result, FromDomain),
        };

    private static AgentToolResult FromDomain(Domain.AgentToolResult value)
        => new()
        {
            BackendId = FromReference(value.BackendId, item => (Identifier)item), Content = FromReference(value.Content, item => new ContentText { Value = item }),
            ErrorCode = FromReference(value.ErrorCode, item => (TextValue)item), IsError = value.IsError,
            PresentationPayloadJson = FromReference(value.PresentationPayloadJson, item => new JsonText { Value = item }),
            RequiresPromptContextRefresh = value.RequiresPromptContextRefresh,
            Sources = FromReference(value.Sources, items => new AgentToolSourceItemCollection
            {
                Value = items.Select(item => new AgentToolSourceItem { Snippet = DisplayText.Optional(item.Snippet), Title = item.Title, Url = item.Url }).ToArray(),
            }),
            StructuredPayloadJson = FromReference(value.StructuredPayloadJson, item => new JsonText { Value = item }),
            Summary = value.Summary, ToolId = value.ToolId, WasTruncated = value.WasTruncated,
        };

    internal static AgentToolDescriptor FromDomain(Domain.AgentToolDescriptor value)
        => new()
        {
            ActivationRequirement = FromReference(value.ActivationRequirement, item => new AgentToolActivationRequirement
            { CapabilityId = FromReference(item.CapabilityId, text => (Identifier)text), CapabilityKind = item.CapabilityKind, SourceId = FromReference(item.SourceId, text => (Identifier)text) }),
            Aliases = FromReference(value.Aliases, TextValues.FromDomain), ArgumentsJsonSchema = FromReference(value.ArgumentsJsonSchema, item => (TextValue)item),
            ConcurrencyMode = Enum.Parse<AgentToolConcurrencyMode>(value.ConcurrencyMode.ToString()), Description = value.Description, DisplayName = value.DisplayName,
            IsReadOnly = value.IsReadOnly, Priority = Enum.Parse<AgentToolPriority>(value.Priority.ToString()), RequiresNetwork = value.RequiresNetwork,
            RuntimeInstructions = FromReference(value.RuntimeInstructions, item => new ContentText { Value = item }),
            SelectionGroupDescription = DisplayText.Optional(value.SelectionGroupDescription), SelectionGroupDisplayName = DisplayText.Optional(value.SelectionGroupDisplayName),
            SelectionGroupId = FromReference(value.SelectionGroupId, item => (Identifier)item), SelectionScope = Enum.Parse<AgentToolSelectionScope>(value.SelectionScope.ToString()),
            SourceDisplayName = DisplayText.Optional(value.SourceDisplayName), SourceId = FromReference(value.SourceId, item => (Identifier)item),
            SourceKind = FromReference(value.SourceKind, item => (TextValue)item), ToolId = value.ToolId,
        };
}
