using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Rpc.Public.Control;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Mapping = Sunder.Package.Agent.Rpc.Public.Control.RunControlMapping;
using CatalogMapping = Sunder.Package.Agent.Rpc.Public.Catalog.RuntimeCatalogMapping;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.run.control", ServiceId = "run-control", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "agent.run.control")]
[SunderRpcDomainError("agent.run-control.capability-unavailable", Description = "The active behavior loop does not implement the requested run-control capability.")]
[SunderRpcDomainError("agent.run-control.prompt-context-acknowledgment-unavailable", Description = "The active behavior loop cannot acknowledge prompt context.")]
[SunderRpcDomainError("agent.run-control.default-loop-unavailable", Description = "The active behavior loop cannot delegate to the default loop.")]
[SunderRpcRuntimeUse(Required = true, Actions = ["discover", "invoke", "subscribe"])]
public sealed partial class RunControlService(Protocol.AgentRunControlRegistry registry)
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<Flag> IsCurrentAsync(RunControlInvocation request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new Flag { Value = Resolve(context, request).IsCurrentRun() });

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<AgentTurnRecordCollection> ListTurnsAsync(RunControlInvocation request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new AgentTurnRecordCollection { Value = Resolve(context, request).ListTurns().Select(CatalogMapping.Turn).ToArray() });

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public ValueTask<AgentTurnRecordCollection> ListRecentTurnsAsync(RunControlRecentTurnsRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new AgentTurnRecordCollection { Value = Resolve(context, request.Invocation).ListRecentTurns(checked((int)request.Limit.Value)).Select(CatalogMapping.Turn).ToArray() });

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public async ValueTask<AgentBehaviorInstructionContext> BuildInstructionContextAsync(RunControlInvocation request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => Mapping.FromDomain(await Resolve(context, request).BuildInstructionContextAsync(cancellationToken).ConfigureAwait(false));

    [SunderRpcMethod(Errors = ["agent.run-control.capability-unavailable"])]
    public ValueTask<Unit> SelectSessionContextAsync(RunControlSessionProjectionRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
    {
        RequireCapability<IAgentSessionContextSelectionRuntime>(Resolve(context, request.Invocation), "session context selection")
            .SelectSessionContextProjection(Mapping.ToDomain(request.Projection));
        return ValueTask.FromResult(new Unit());
    }

    [SunderRpcMethod(Errors = ["agent.run-control.capability-unavailable"])]
    public ValueTask<Unit> ReportActivityAsync(RunControlActivityRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
    {
        RequireCapability<IAgentRunActivitySink>(Resolve(context, request.Invocation), "run activity")
            .ReportRunActivity(Enum.Parse<Domain.AgentRunActivityKind>(request.Kind.ToString()), request.Text.Value);
        return ValueTask.FromResult(new Unit());
    }

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["agent.run-control.capability-unavailable"])]
    public ValueTask<AgentRunBudgetState> GetRunBudgetAsync(RunControlInvocation request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Mapping.FromDomain(RequireCapability<IAgentRunBudgetRuntime>(Resolve(context, request), "run budget").GetRunBudgetState()));

    [SunderRpcMethod(Errors = ["agent.run-control.capability-unavailable"])]
    public ValueTask<AgentRunBudgetState> ChargeRunBudgetAsync(RunControlBudgetChargeRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Mapping.FromDomain(RequireCapability<IAgentRunBudgetRuntime>(Resolve(context, request.Invocation), "run budget")
            .ChargeRunBudget(new(request.Charge.ProviderCycles.Value, request.Charge.ToolCalls.Value, request.Charge.SubmittedContextTokens.Value))));

    [SunderRpcMethod(Errors = ["agent.run-control.prompt-context-acknowledgment-unavailable"])]
    public async ValueTask<Unit> AcknowledgePromptContextAsync(RunControlPromptContextRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
    {
        if (Resolve(context, request.Invocation) is not IAgentPromptContextAcknowledgmentRuntime runtime)
            throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "agent.run-control.prompt-context-acknowledgment-unavailable",
                "The Agent run-control provider cannot acknowledge prompt context."));
        await runtime.AcknowledgePromptContextAsync(request.Blocks.Value.Select(value => new Domain.AgentPromptContextReceiptBlock(
            value.HostIdentity.Value, value.ContextIdentity.Value, value.DocumentPath.Value, value.ContentHash.Value)).ToArray(), cancellationToken).ConfigureAwait(false);
        return new();
    }

    [SunderRpcMethod]
    public ValueTask<Unit> DiscardPromptContextAsync(RunControlInvocation request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
    {
        if (Resolve(context, request) is IAgentPromptContextAcknowledgmentRuntime runtime) runtime.DiscardPromptContextAcknowledgment();
        return ValueTask.FromResult(new Unit());
    }

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    public async ValueTask<AgentToolDescriptorCollection> ListReadyToolsAsync(RunControlInvocation request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => new() { Value = (await Resolve(context, request).ListReadyToolsAsync(cancellationToken).ConfigureAwait(false)).Select(value => Mapping.FromDomain(value.Descriptor)).ToArray() };

    [SunderRpcMethod]
    public ValueTask<AgentRunCheckpointRecord> SaveCheckpointAsync(RunControlCheckpointRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CatalogMapping.RunCheckpoint(Resolve(context, request.Invocation).SaveCheckpoint(
            Enum.Parse<Domain.AgentRunStatus>(request.Status.ToString()), request.Summary.IsPresent ? request.Summary.Value.Value : null))!);

    [SunderRpcMethod]
    public ValueTask<Unit> LogEventAsync(RunControlLogRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
    {
        Mapping.WriteLog(Resolve(context, request.Invocation), request);
        return ValueTask.FromResult(new Unit());
    }

    [SunderRpcMethod]
    public ValueTask<AgentTurnRecord> UpsertAssistantTurnAsync(RunControlAssistantTurnRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CatalogMapping.Turn(Resolve(context, request.Invocation).UpsertAssistantTurn(
            request.AssistantTurn.IsPresent ? Mapping.ToDomain(request.AssistantTurn.Value) : null, request.Content.Value)));

    [SunderRpcMethod]
    public ValueTask<AgentTurnRecord> CompleteAssistantTurnAsync(RunControlCompleteTurnRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CatalogMapping.Turn(Resolve(context, request.Invocation).CompleteAssistantTurn(Mapping.ToDomain(request.AssistantTurn))));

    [SunderRpcMethod]
    public async ValueTask<Unit> PublishLifecycleAsync(RunControlLifecycleRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
    {
        await Resolve(context, request.Invocation).PublishLifecycleEventAsync(
            Enum.Parse<Domain.AgentLifecycleEventKind>(request.Kind.ToString()), Enum.Parse<Domain.AgentRunStatus>(request.Status.ToString()),
            request.TriggerTurn.IsPresent ? Mapping.ToDomain(request.TriggerTurn.Value) : null,
            request.Checkpoint.IsPresent ? Mapping.ToDomain(request.Checkpoint.Value) : null, request.IsInterrupted.Value, cancellationToken).ConfigureAwait(false);
        return new();
    }

    [SunderRpcMethod]
    public async ValueTask<AgentToolCallOutcome> InvokeToolAsync(RunControlToolRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => Mapping.FromDomain(await Resolve(context, request.Invocation).InvokeToolAsync(Mapping.ToDomain(request.ToolCall),
            request.AssistantTurn.IsPresent ? Mapping.ToDomain(request.AssistantTurn.Value) : null, cancellationToken).ConfigureAwait(false));

    [SunderRpcMethod]
    public async ValueTask<AgentToolCallOutcomeCollection> InvokeToolsAsync(RunControlToolsRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
        => new() { Value = (await Resolve(context, request.Invocation).InvokeToolsAsync(request.ToolCalls.Value.Select(Mapping.ToDomain).ToArray(),
            request.AssistantTurn.IsPresent ? Mapping.ToDomain(request.AssistantTurn.Value) : null, cancellationToken).ConfigureAwait(false)).Select(Mapping.FromDomain).ToArray() };

    [SunderRpcMethod(Errors = ["agent.run-control.default-loop-unavailable"])]
    public async ValueTask<AgentBehaviorLoopResult> RunDefaultAsync(RunControlDefaultLoopRequest request, SunderRpcInvocationContext context, CancellationToken cancellationToken = default)
    {
        if (Resolve(context, request.Invocation) is not IAgentInnerBehaviorLoopRuntime runtime)
            throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "agent.run-control.default-loop-unavailable", "The default behavior loop is unavailable."));
        return Mapping.FromDomain(await runtime.RunDefaultLoopAsync(Mapping.ToDomain(request.Context), cancellationToken).ConfigureAwait(false));
    }

    private IAgentBehaviorLoopRuntime Resolve(SunderRpcInvocationContext context, RunControlInvocation invocation)
        => registry.Resolve(invocation.InvocationId.Value, context.CallerPackageId);

    private static TCapability RequireCapability<TCapability>(IAgentBehaviorLoopRuntime runtime, string displayName) where TCapability : class
        => runtime as TCapability ?? throw new SunderRpcException(new(SunderRpcErrorKind.Domain,
            "agent.run-control.capability-unavailable", $"The Agent run-control provider does not support {displayName}."));
}
