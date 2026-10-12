using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Services;

[SunderService(SunderServiceExposure.Public, Key = "run-control", ContractId = "sunder.agent.run.control", ServiceId = "run-control", IncludeSchemaDialect = true)]
[SunderServiceProvider(ProviderId = "agent.run.control")]
[SunderServiceError("agent.run-control.capability-unavailable", Description = "The active behavior loop does not implement the requested run-control capability.")]
[SunderServiceError("agent.run-control.prompt-context-acknowledgment-unavailable", Description = "The active behavior loop cannot acknowledge prompt context.")]
[SunderServiceError("agent.run-control.default-loop-unavailable", Description = "The active behavior loop cannot delegate to the default loop.")]
[SunderAppUse(Enabled = false)]
[SunderRuntimeUse(Required = true, Actions = ["discover", "invoke", "subscribe"])]
public sealed partial class AgentRunControlService(AgentActiveRunRegistry runs)
{
    private IAgentBehaviorLoopRuntime Resolve(Guid invocationId, SunderRpcInvocationContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return runs.ResolveBehaviorInvocation(invocationId, context.CallerPackageId);
    }

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<bool> IsCurrentAsync(Guid invocationId, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Resolve(invocationId, context, cancellationToken).IsCurrentRun());
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListTurnsAsync(Guid invocationId, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Resolve(invocationId, context, cancellationToken).ListTurns());
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListRecentTurnsAsync(Guid invocationId,
        [SunderValueConstraints(Minimum = "1", Maximum = "100")] int limit, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Resolve(invocationId, context, cancellationToken).ListRecentTurns(limit));
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<AgentBehaviorInstructionContext> BuildInstructionContextAsync(Guid invocationId, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => Resolve(invocationId, context, cancellationToken).BuildInstructionContextAsync(cancellationToken);
    [SunderMethod(Errors = ["agent.run-control.capability-unavailable"])]
    public ValueTask SelectSessionContextAsync(Guid invocationId, AgentSessionPromptProjection projection, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
    {
        Require<IAgentSessionContextSelectionRuntime>(Resolve(invocationId, context, cancellationToken)).SelectSessionContextProjection(projection);
        return ValueTask.CompletedTask;
    }
    [SunderMethod(Errors = ["agent.run-control.capability-unavailable"])]
    public ValueTask ReportActivityAsync(Guid invocationId, AgentRunActivityKind kind, string text, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
    {
        Require<IAgentRunActivitySink>(Resolve(invocationId, context, cancellationToken)).ReportRunActivity(kind, text);
        return ValueTask.CompletedTask;
    }
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent, Errors = ["agent.run-control.capability-unavailable"])]
    public ValueTask<AgentRunBudgetState> GetRunBudgetAsync(Guid invocationId, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Require<IAgentRunBudgetRuntime>(Resolve(invocationId, context, cancellationToken)).GetRunBudgetState());
    [SunderMethod(Errors = ["agent.run-control.capability-unavailable"])]
    public ValueTask<AgentRunBudgetState> ChargeRunBudgetAsync(Guid invocationId, AgentRunBudgetCharge charge, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Require<IAgentRunBudgetRuntime>(Resolve(invocationId, context, cancellationToken)).ChargeRunBudget(charge));
    [SunderMethod(Errors = ["agent.run-control.prompt-context-acknowledgment-unavailable"])]
    public ValueTask AcknowledgePromptContextAsync(Guid invocationId, IReadOnlyList<AgentPromptContextReceiptBlock> blocks, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => Require<IAgentPromptContextAcknowledgmentRuntime>(Resolve(invocationId, context, cancellationToken),
            "agent.run-control.prompt-context-acknowledgment-unavailable").AcknowledgePromptContextAsync(blocks, cancellationToken);
    public ValueTask DiscardPromptContextAsync(Guid invocationId, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
    {
        if (Resolve(invocationId, context, cancellationToken) is IAgentPromptContextAcknowledgmentRuntime runtime)
            runtime.DiscardPromptContextAcknowledgment();
        return ValueTask.CompletedTask;
    }
    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public async ValueTask<IReadOnlyList<AgentToolDescriptor>> ListReadyToolsAsync(Guid invocationId, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => (await Resolve(invocationId, context, cancellationToken).ListReadyToolsAsync(cancellationToken).ConfigureAwait(false))
            .Select(tool => tool.Descriptor).ToArray();
    public ValueTask<AgentRunCheckpointRecord> SaveCheckpointAsync(Guid invocationId, AgentRunStatus status, string? summary, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Resolve(invocationId, context, cancellationToken).SaveCheckpoint(status, summary));
    public ValueTask LogEventAsync(Guid invocationId,
        [SunderValueConstraints(AllowedStrings = ["trace", "debug", "information", "warning", "error", "critical"])] string level,
        string eventName, string message, [SunderValueConstraints(Minimum = "0")] long? elapsedMilliseconds,
        IReadOnlyDictionary<string, JsonElement>? attributes, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
    {
        var runtime = Resolve(invocationId, context, cancellationToken);
        runtime.LogEvent(Enum.Parse<PackageLogLevel>(level, ignoreCase: true), eventName, message, elapsedMilliseconds,
            attributes?.ToDictionary(item => item.Key, item => (object?)item.Value.Clone(), StringComparer.Ordinal));
        return ValueTask.CompletedTask;
    }
    public ValueTask<AgentTurnRecord> UpsertAssistantTurnAsync(Guid invocationId, AgentTurnRecord? assistantTurn, string content, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Resolve(invocationId, context, cancellationToken).UpsertAssistantTurn(assistantTurn, content));
    public ValueTask<AgentTurnRecord> CompleteAssistantTurnAsync(Guid invocationId, AgentTurnRecord assistantTurn, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Resolve(invocationId, context, cancellationToken).CompleteAssistantTurn(assistantTurn));
    public ValueTask PublishLifecycleAsync(Guid invocationId, AgentLifecycleEventKind kind, AgentRunStatus status,
        AgentTurnRecord? triggerTurn, AgentRunCheckpointRecord? checkpoint, bool isInterrupted, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => Resolve(invocationId, context, cancellationToken).PublishLifecycleEventAsync(kind, status, triggerTurn, checkpoint, isInterrupted, cancellationToken);
    [SunderMethod(TimeoutSeconds = 2100)]
    public ValueTask<AgentToolCallOutcome> InvokeToolAsync(Guid invocationId, AgentToolCallRequest toolCall, AgentTurnRecord? assistantTurn, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => Resolve(invocationId, context, cancellationToken).InvokeToolAsync(toolCall, assistantTurn, cancellationToken);
    [SunderMethod(TimeoutSeconds = 2100)]
    public ValueTask<IReadOnlyList<AgentToolCallOutcome>> InvokeToolsAsync(Guid invocationId, IReadOnlyList<AgentToolCallRequest> toolCalls,
        AgentTurnRecord? assistantTurn, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => Resolve(invocationId, context, cancellationToken).InvokeToolsAsync(toolCalls, assistantTurn, cancellationToken);
    [SunderMethod(Errors = ["agent.run-control.default-loop-unavailable"], TimeoutSeconds = 2100)]
    public ValueTask<AgentBehaviorLoopResult> RunDefaultAsync(Guid invocationId, AgentBehaviorLoopContext loopContext, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => Require<IAgentInnerBehaviorLoopRuntime>(Resolve(invocationId, context, cancellationToken), "agent.run-control.default-loop-unavailable")
            .RunDefaultLoopAsync(loopContext, cancellationToken);

    public async IAsyncEnumerable<AgentChatStreamEvent> StreamChatAsync(Guid invocationId, AgentChatStreamRequest request,
        IReadOnlyDictionary<string, JsonElement>? correlationAttributes, SunderRpcInvocationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var runtime = Resolve(invocationId, context, cancellationToken);
        IChatClient? chat = null;
        IAsyncEnumerator<ChatResponseUpdate>? updates = null;
        AgentChatProviderException? providerFailure = null;
        Exception? streamFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            try
            {
                chat = await runtime.CreateChatClientAsync(new(request.ProviderId, request.ModelId,
                    CorrelationAttributes: correlationAttributes?.ToDictionary(item => item.Key, item => (object?)item.Value.Clone(), StringComparer.Ordinal)), cancellationToken).ConfigureAwait(false);
                var messages = await AgentChatMapper.ToChatMessagesAsync(request.Messages, cancellationToken).ConfigureAwait(false);
                updates = chat.GetStreamingResponseAsync(messages, AgentChatMapper.ToChatOptions(request.Options), cancellationToken).GetAsyncEnumerator(cancellationToken);
            }
            catch (AgentChatProviderException exception) { providerFailure = exception; }
            catch (Exception exception) { streamFailure = exception; }

            while (providerFailure is null && streamFailure is null && updates is not null)
            {
                AgentChatUpdateEvent? update = null;
                var hasNext = false;
                try
                {
                    hasNext = await updates.MoveNextAsync().ConfigureAwait(false);
                    if (hasNext) update = AgentChatMapper.FromUpdate(updates.Current);
                }
                catch (AgentChatProviderException exception) { providerFailure = exception; }
                catch (Exception exception) { streamFailure = exception; }
                if (!hasNext || providerFailure is not null || streamFailure is not null) break;
                try { yield return update!; }
                finally { await AgentChatMapper.DisposeContentAsync(update!.ContentItems ?? []).ConfigureAwait(false); }
            }
        }
        finally
        {
            if (updates is not null)
            {
                try { await updates.DisposeAsync().ConfigureAwait(false); }
                catch (AgentChatProviderException exception) { providerFailure ??= exception; }
                catch (Exception exception) { cleanupFailure ??= exception; }
            }
            if (chat is not null)
            {
                try { chat.Dispose(); }
                catch (AgentChatProviderException exception) { providerFailure ??= exception; }
                catch (Exception exception) { cleanupFailure ??= exception; }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (streamFailure is not null) ExceptionDispatchInfo.Capture(streamFailure).Throw();
        if (providerFailure is not null)
            yield return AgentChatStreamEvent.Error(providerFailure.ErrorCode ?? "provider-error", providerFailure.Message, providerFailure.Content, providerFailure.FailureKind);
        else if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private static T Require<T>(IAgentBehaviorLoopRuntime runtime, string code = "agent.run-control.capability-unavailable") where T : class
        => runtime as T ?? throw new SunderRpcException(new(SunderRpcErrorKind.Domain, code, "The active run does not support this operation."));
}
