using Domain = Sunder.Package.Agent.Contracts.Models;
using Context = Sunder.Package.Agent.Contracts;
using Mapping = Sunder.Package.Agent.Rpc.Public.Control.RunControlMapping;

namespace Sunder.Package.Agent.Rpc.Public;

internal static class PromptContextMapping
{
    internal static Domain.AgentPromptContextRequest ToDomain(PromptContextRequestWire value, Protocol.AgentRpcCatalog? catalog)
    {
        var session = new Context.AgentSessionContextRecord(value.Session.SessionId.Value, value.Session.ProfileId.Value,
            value.Session.ProfileDisplayName.Value, value.Session.SessionTitle.Value, Enum.Parse<Domain.AgentSessionState>(value.Session.SessionState.ToString()),
            Mapping.Reference(value.Session.WorkingSummary, item => item.Value));
        var run = new Context.AgentRunContextRecord(value.Run.RunId.Value, value.Run.Revision.Value,
            Enum.Parse<Domain.AgentRunStatus>(value.Run.Status.ToString()), value.Run.IsInterrupted.Value, value.Run.StartedAtUtc.ToDomain());
        // ShouldContribute is a read-only domain projection of Intent. Preserve that
        // derivation rather than accepting a contradictory wire flag as authority.
        var plan = new Domain.AgentPromptContextPlan(value.ContextPlan.Intent.Value, value.ContextPlan.QueryText.Value,
            Mapping.Reference(value.ContextPlan.Reason, item => item.Value),
            Mapping.Reference(value.ContextPlan.PreferredCategories, items => items.Value.Select(item => item.Value).ToArray()),
            checked((int)value.ContextPlan.MaxEntryCount.Value), checked((int)value.ContextPlan.MaxChars.Value));
        return new(session, run,
            new Context.AgentTurnContextRecord(session, run, value.Turn.UserMessage.Value, Mapping.Reference(value.Turn.WorkingSummary, item => item.Value)),
            value.Turns.Value.Select(Mapping.ToDomain).ToArray(), value.RecentLiveBufferTurns.Value.Select(Mapping.ToDomain).ToArray(), plan)
        {
            Profile = Mapping.Reference(value.Profile, Mapping.ToDomain),
            Workspace = Mapping.Reference(value.Workspace, Mapping.ToDomain),
            ExecutionBinding = Mapping.Reference(value.ExecutionBinding, Mapping.ToDomain),
            AvailableTools = value.AvailableTools.Value.Select(Mapping.ToDomain).ToArray(),
            MemoryConsistencyBarrier = Mapping.Reference(value.MemoryConsistencyBarrier, item => new Domain.AgentMemoryConsistencyBarrier(item.EventId.Value, item.PayloadHash.Value)),
            TranscriptEpoch = value.TranscriptEpoch.Value,
            ExecutionTargetReference = value.ExecutionTarget.IsPresent && catalog is not null
                && catalog.TryGetServiceReference(Sunder.Package.Agent.Runtime.Rpc.AgentExecutionBindings.Service, value.ExecutionTarget.Value.ToDomain(), out var reference)
                    ? reference : null,
        };
    }

    internal static Domain.AgentPromptContextReceipt ToDomain(AgentPromptContextReceipt value)
        => new(value.SessionId.Value, value.RunId.Value, value.TranscriptEpoch.Value,
            value.Blocks.Value.Select(item => new Domain.AgentPromptContextReceiptBlock(item.HostIdentity.Value, item.ContextIdentity.Value,
                item.DocumentPath.Value, item.ContentHash.Value)).ToArray());
}
