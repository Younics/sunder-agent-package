using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    internal class RunControlProbe : IAgentBehaviorLoopRuntime
    {
        public AgentBehaviorLoopContext Context { get; } = CreateRunControlContext();
        public AgentTurnRecord Turn { get; }
        public JsonElement Arguments { get; private set; }
        public int Calls { get; private set; }
        public bool Current { get; set; } = true;
        public Exception? Failure { get; set; }
        public bool Block { get; set; }
        public TaskCompletionSource Started { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<IChatClient> ChatFactory { get; set; } = () => throw new InvalidOperationException("No chat configured.");
        public string ToolSchema { get; set; } = "{opaque-schema";

        public RunControlProbe()
        {
            var turnId = Guid.NewGuid();
            Turn = new(turnId, Context.Session.SessionId, AgentMessageRole.Assistant, AgentTurnKind.Message,
                [new(Guid.NewGuid(), turnId, 0, AgentTurnItemKind.ToolResult, "Text 漢字", "call.one", "tool.one", "{opaque",
                    "Summary", "{structured", "[sources", true, false, "", "backend", "{presentation")
                {
                    ToolExecutionId = Guid.NewGuid(), ToolExecutionStatus = AgentToolExecutionStatus.Ambiguous,
                    ToolOwnerPackageId = "package.tool", ToolSchemaId = "schema", ToolSchemaVersion = "1.2.3", ToolHeaderHint = "Hint",
                    ToolErrorSummary = "", ToolDetailRevision = 17, ToolHasDetails = false, IsToolHeaderProjection = true,
                }], Context.RunStartedAtUtc, Context.RunStartedAtUtc)
            { ContentRevision = 18, RunId = Context.RunId, RunRevision = Context.RunRevision, IsStreaming = true };
        }

        protected void Record(object value)
        {
            Calls++;
            Arguments = JsonSerializer.SerializeToElement(value, ExecutionJson);
            if (Failure is not null) throw Failure;
        }
        protected async ValueTask Wait(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Block) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        }
        public bool IsCurrentRun() { Record(new { }); return Current; }
        public IReadOnlyList<AgentTurnRecord> ListTurns() { Record(new { }); return [Turn]; }
        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(int limit) { Record(new { limit }); return [Turn]; }
        public async ValueTask<AgentBehaviorInstructionContext> BuildInstructionContextAsync(CancellationToken cancellationToken = default)
        {
            Record(new { }); await Wait(cancellationToken);
            return new("Trusted system", false, [new("Untrusted reference", "Body 漢字", -2, "source.one", AgentContextProvenance.Tool, AgentContextTrust.Untrusted)
            {
                Authority = AgentPromptContextAuthority.ScopedInstruction, Usage = AgentPromptContextUsage.ScopedInstruction, HostIdentity = "host.opaque",
                Scope = new("/workspace", "/workspace/src", "/workspace/AGENTS.md", new string('a', 64), "context.one"),
            }]);
        }
        public async ValueTask<IReadOnlyList<AgentRuntimeTool>> ListReadyToolsAsync(CancellationToken cancellationToken = default)
        {
            Record(new { }); await Wait(cancellationToken);
            var tool = new AgentToolDescriptor("tool.one", "Tool 漢字", "Description", false, true, ToolSchema, "native", "source.one", "Source",
                ["alias"], AgentToolSelectionScope.Group, "group.one", "Group", "", "Instructions", new("cap.kind", "cap.id", "cap.source"), AgentToolPriority.High)
            { ConcurrencyMode = AgentToolConcurrencyMode.ParallelSafe };
            return [new(tool, AIFunctionFactory.CreateDeclaration("tool.one", "Description", JsonSerializer.SerializeToElement(new { type = "object" })))];
        }
        public async ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
        {
            Record(new { context.ProviderId, context.ModelId, context.CorrelationAttributes });
            await Wait(cancellationToken);
            return ChatFactory();
        }
        public AgentRunCheckpointRecord SaveCheckpoint(AgentRunStatus status, string? summary)
        { Record(new { status, summary }); return Context.RunningCheckpoint with { Status = status, Summary = summary }; }
        public void LogEvent(PackageLogLevel level, string eventName, string message, long? elapsedMilliseconds = null,
            IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null)
            => Record(new { level, eventName, message, elapsedMilliseconds, attributes, exception = exception?.Message });
        public AgentTurnRecord UpsertAssistantTurn(AgentTurnRecord? assistantTurn, string content)
        { Record(new { assistantTurn, content }); return Turn; }
        public AgentTurnRecord CompleteAssistantTurn(AgentTurnRecord assistantTurn)
        { Record(new { assistantTurn }); return Turn with { IsStreaming = false }; }
        public async ValueTask PublishLifecycleEventAsync(AgentLifecycleEventKind kind, AgentRunStatus status, AgentTurnRecord? triggerTurn = null,
            AgentRunCheckpointRecord? checkpoint = null, bool isInterrupted = false, CancellationToken cancellationToken = default)
        { Record(new { kind, status, triggerTurn, checkpoint, isInterrupted }); await Wait(cancellationToken); }
        public async ValueTask<AgentToolCallOutcome> InvokeToolAsync(AgentToolCallRequest toolCall, AgentTurnRecord? assistantTurn, CancellationToken cancellationToken = default)
        { Record(new { toolCall, assistantTurn }); await Wait(cancellationToken); return Outcome(toolCall); }
        public async ValueTask<IReadOnlyList<AgentToolCallOutcome>> InvokeToolsAsync(IReadOnlyList<AgentToolCallRequest> toolCalls,
            AgentTurnRecord? assistantTurn, CancellationToken cancellationToken = default)
        { Record(new { toolCalls, assistantTurn }); await Wait(cancellationToken); return toolCalls.Select(Outcome).ToArray(); }

        private AgentToolCallOutcome Outcome(AgentToolCallRequest call)
            => new(AgentToolCallOutcomeKind.Executed, Context.RunningCheckpoint,
                new(call.ToolId, call.CallId, "Output 漢字", "{opaque-json", [new("Source", "https://example.test", "")], true, true, "tool.failed", "backend", "{presentation")
                { RequiresPromptContextRefresh = true });
    }

    internal sealed class FullRunControlProbe : RunControlProbe, IAgentInnerBehaviorLoopRuntime, IAgentRunBudgetRuntime,
        IAgentRunActivitySink, IAgentSessionContextSelectionRuntime, IAgentPromptContextAcknowledgmentRuntime
    {
        public void SelectSessionContextProjection(AgentSessionPromptProjection projection) => Record(new { projection });
        public void ReportRunActivity(AgentRunActivityKind kind, string text) => Record(new { kind, text });
        public AgentRunBudgetState GetRunBudgetState() { Record(new { }); return new(2, 3, 4000); }
        public AgentRunBudgetState ChargeRunBudget(AgentRunBudgetCharge charge)
        { Record(new { charge }); return new(charge.ProviderCycles, charge.ToolCalls, charge.SubmittedContextTokens); }
        public async ValueTask AcknowledgePromptContextAsync(IReadOnlyList<AgentPromptContextReceiptBlock> blocks, CancellationToken cancellationToken)
        { Record(new { blocks }); await Wait(cancellationToken); }
        public void DiscardPromptContextAcknowledgment() => Record(new { });
        public async ValueTask<AgentBehaviorLoopResult> RunDefaultLoopAsync(AgentBehaviorLoopContext context, CancellationToken cancellationToken = default)
        { Record(new { context }); await Wait(cancellationToken); return new(context.RunningCheckpoint, AgentBehaviorLoopCompletionKind.WaitingForApproval); }
    }

    private static AgentBehaviorLoopContext CreateRunControlContext()
    {
        var now = new DateTimeOffset(2026, 9, 18, 12, 34, 56, TimeSpan.FromHours(2)).AddTicks(1234567);
        var sessionId = Guid.NewGuid();
        var session = new AgentSessionRecord(sessionId, "Session 漢字", AgentSessionState.Active, now, now, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), 2, "parent.call", "task.one", "profile.one", "loop.one", "subagent", "workspace.one");
        var profile = new AgentProfileRecord("profile.one", "Profile", "", "Instructions", "provider.one", "model.one", "embedding.one", "embedding.model", now, now,
            [new("profile.one", AgentModelCapabilityKinds.Chat, "provider.one", "model.one", "{opaque", now)],
            [new("tools", "tool.one", "source.one")], "loop.one", "source.loop", "{loop-settings", true);
        var workspace = new AgentWorkspaceRecord("workspace.one", "Workspace", "", now, now,
            [new("path.one", "workspace.one", "/workspace", true, -1, now, now)],
            [new("document.one", "workspace.one", "/workspace/AGENTS.md", 2, now, now)]);
        return new(session, profile, "provider.one", "model.one", new(true, false, true, "Capabilities", true, false, true, false, 64000, 4000),
            workspace, new("binding.one", workspace.WorkspaceId, "execution", "local", "primary", true, -1, now, now), Guid.NewGuid(), 7,
            new(Guid.NewGuid(), sessionId, 7, AgentRunStatus.Running, "Running", now), now, "User message", Guid.NewGuid(),
            new("variant.one", "Variant", "", AgentReasoningEffort.ExtraHigh), new("speed.one", "Speed", ""), new("mode.one", "Mode", "", true));
    }
}
