using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Catalog = Sunder.Package.Agent.Rpc.Public.Catalog;

namespace Sunder.Package.Agent.Rpc.Public.Control;

internal static partial class RunControlMapping
{
    internal static void WriteLog(Contracts.Contracts.IAgentBehaviorLoopRuntime runtime, RunControlLogRequest request)
        => runtime.LogEvent(Enum.Parse<Sunder.Sdk.Logging.PackageLogLevel>(request.Level.ToString()),
            request.EventName.Value, request.Message.Value, Nullable(request.ElapsedMilliseconds, item => item.Value),
            Reference(request.Attributes, item => item.Value.ToDictionary(pair => pair.Key, pair => (object?)pair.Value.Value, StringComparer.Ordinal)));

    internal static AgentRunBudgetState FromDomain(Domain.AgentRunBudgetState value)
        => new() { ProviderCycles = new() { Value = value.ProviderCycles }, SubmittedContextTokens = new() { Value = value.SubmittedContextTokens }, ToolCalls = new() { Value = value.ToolCalls } };

    internal static AgentBehaviorLoopResult FromDomain(Domain.AgentBehaviorLoopResult value)
        => new() { Checkpoint = Catalog.RuntimeCatalogMapping.RunCheckpoint(value.Checkpoint)!, CompletionKind = Enum.Parse<AgentBehaviorLoopCompletionKind>(value.CompletionKind.ToString()) };

    internal static Domain.AgentRunCheckpointRecord ToDomain(AgentRunCheckpointRecord value)
        => new(value.CheckpointId.Value, value.SessionId.Value, value.RunRevision.Value, Enum.Parse<Domain.AgentRunStatus>(value.Status.ToString()),
            Reference(value.Summary, item => item.Value), value.CreatedAtUtc.ToDomain());

    internal static Domain.AgentTurnRecord ToDomain(AgentTurnRecord value)
        => new(value.TurnId.Value, value.SessionId.Value, Enum.Parse<Domain.AgentMessageRole>(value.Role.ToString()),
            Enum.Parse<Domain.AgentTurnKind>(value.Kind.ToString()), value.Items.Value.Select(ToDomain).ToArray(),
            value.CreatedAtUtc.ToDomain(), value.UpdatedAtUtc.ToDomain())
        {
            ContentRevision = value.ContentRevision.Value, IsStreaming = value.IsStreaming.Value,
            RunId = Nullable(value.RunId, item => item.Value), RunRevision = Nullable(value.RunRevision, item => item.Value),
        };

    private static Domain.AgentTurnItemRecord ToDomain(AgentTurnItemRecord value)
        => new(value.ItemId.Value, value.TurnId.Value, checked((int)value.SequenceNumber.Value), Enum.Parse<Domain.AgentTurnItemKind>(value.Kind.ToString()),
            Reference(value.TextContent, item => item.Value), Reference(value.CallId, item => item.Value), Reference(value.ToolId, item => item.Value),
            Reference(value.ArgumentsJson, item => item.Value), Reference(value.ResultSummary, item => item.Value), Reference(value.StructuredPayloadJson, item => item.Value),
            Reference(value.SourcesJson, item => item.Value), value.WasTruncated.Value, value.IsError.Value, Reference(value.ErrorCode, item => item.Value),
            Reference(value.BackendId, item => item.Value), Reference(value.PresentationPayloadJson, item => item.Value))
        {
            ToolExecutionId = Nullable(value.ToolExecutionId, item => item.Value),
            ToolExecutionStatus = Nullable(value.ToolExecutionStatus, item => Enum.Parse<Domain.AgentToolExecutionStatus>(item.ToString())),
            ToolOwnerPackageId = Reference(value.ToolOwnerPackageId, item => item.Value), ToolSchemaId = Reference(value.ToolSchemaId, item => item.Value),
            ToolSchemaVersion = Reference(value.ToolSchemaVersion, item => item.Value), ToolHeaderHint = Reference(value.ToolHeaderHint, item => item.Value),
            ToolErrorSummary = Reference(value.ToolErrorSummary, item => item.Value), ToolDetailRevision = value.ToolDetailRevision.Value,
            ToolHasDetails = Nullable(value.ToolHasDetails, item => item.Value), IsToolHeaderProjection = value.IsToolHeaderProjection.Value,
        };

    internal static Domain.AgentSessionPromptProjection ToDomain(AgentSessionPromptProjection value)
        => new(value.PromptTurns.Value.Select(ToDomain).ToArray(), value.SummaryUpdated.Value, checked((int)value.OmittedHistoricalTurnCount.Value),
            Reference(value.ContextCheckpoint, ToDomain));

    private static Domain.AgentSessionContextCheckpointRecord ToDomain(Catalog.AgentSessionContextCheckpointRecord value)
        => new(value.ContextCheckpointId.Value, value.SessionId.Value, Nullable(value.FirstOmittedTurnId, item => item.Value),
            Nullable(value.LastOmittedTurnId, item => item.Value), checked((int)value.OmittedTurnCount.Value), value.SummaryText.Value,
            Reference(value.DetailsJson, item => item.Value), value.CreatedAtUtc.ToDomain());

    internal static AgentBehaviorInstructionContext FromDomain(Domain.AgentBehaviorInstructionContext value)
        => new()
        {
            HasSupplementaryContext = value.HasSupplementaryContext,
            SystemInstructions = FromReference(value.SystemInstructions, item => new ContentText { Value = item }),
            SupplementaryContextBlocks = FromReference(value.SupplementaryContextBlocks, items => new AgentPromptContextBlockCollection
            { Value = items.Select(FromDomain).ToArray() }),
        };

    internal static AgentPromptContextBlock FromDomain(Domain.AgentPromptContextBlock value)
        => new()
        {
            Authority = Enum.Parse<AgentPromptContextAuthority>(value.Authority.ToString()), Content = new() { Value = value.Content },
            HostIdentity = FromReference(value.HostIdentity, item => (TextValue)item), Priority = value.Priority,
            Provenance = Enum.Parse<AgentContextProvenance>(value.Provenance.ToString()), SourceId = FromReference(value.SourceId, item => (Identifier)item),
            Title = value.Title, Trust = Enum.Parse<AgentContextTrust>(value.Trust.ToString()), Usage = Enum.Parse<AgentPromptContextUsage>(value.Usage.ToString()),
            Scope = FromReference(value.Scope, item => new AgentPromptContextScope
            {
                AppliesToDirectory = item.AppliesToDirectory, ContentHash = item.ContentHash, ContextIdentity = item.ContextIdentity,
                DocumentPath = item.DocumentPath, ScopeRoot = item.ScopeRoot,
            }),
        };

    internal static TDomain? Reference<TValue, TDomain>(SunderRpcOptional<TValue> value, Func<TValue, TDomain> convert) where TDomain : class
        => value.IsPresent ? convert(value.Value) : null;

    internal static TDomain? Nullable<TValue, TDomain>(SunderRpcOptional<TValue> value, Func<TValue, TDomain> convert) where TDomain : struct
        => value.IsPresent ? convert(value.Value) : null;

    internal static SunderRpcOptional<TValue> FromReference<TDomain, TValue>(TDomain? value, Func<TDomain, TValue> convert) where TDomain : class
        => value is null ? default(SunderRpcOptional<TValue>) : new SunderRpcOptional<TValue>(convert(value));

    internal static SunderRpcOptional<TValue> FromNullable<TDomain, TValue>(TDomain? value, Func<TDomain, TValue> convert) where TDomain : struct
        => value is null ? default(SunderRpcOptional<TValue>) : new SunderRpcOptional<TValue>(convert(value.Value));
}
