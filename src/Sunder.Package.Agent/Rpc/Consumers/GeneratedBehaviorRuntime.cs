using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.RunControl;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed partial class GeneratedBehaviorRuntime(AgentRpcCatalog catalog, Guid invocationId, AgentRpcProviderHandle selected)
    : IAgentBehaviorLoopRuntime, IAgentInnerBehaviorLoopRuntime, IAgentSessionContextSelectionRuntime,
        IAgentRunActivitySink, IAgentRunBudgetRuntime, IAgentPromptContextAcknowledgmentRuntime
{
    private readonly AgentRpcReference<GeneratedRunControlEndpoint> _control = catalog.GetServiceReference(AgentBehaviorBindings.RunControls, selected);
    private Wire.RunControlInvocation Invocation => new() { InvocationId = invocationId };
    private static T Map<T>(object value) where T : class => AgentConsumerValues.Convert<T>(value);
    private static T? Optional<T>(object? value) where T : class => value is null ? null : Map<T>(value);
    private static AgentRunBudgetState Budget(Wire.AgentRunBudgetState value) => new(value.ProviderCycles, value.ToolCalls, value.SubmittedContextTokens);

    public bool IsCurrentRun() => Invoke((client, token) => client.IsCurrentAsync(Invocation, token)).AsTask().GetAwaiter().GetResult();
    public IReadOnlyList<AgentTurnRecord> ListTurns() => Invoke(async (client, token) => Map<AgentTurnRecord[]>(await client.ListTurnsAsync(Invocation, token).ConfigureAwait(false))).AsTask().GetAwaiter().GetResult();
    public IReadOnlyList<AgentTurnRecord> ListRecentTurns(int limit) => Invoke(async (client, token) => Map<AgentTurnRecord[]>(await client.ListRecentTurnsAsync(new() { Invocation = Invocation, Limit = limit }, token).ConfigureAwait(false))).AsTask().GetAwaiter().GetResult();
    public ValueTask<AgentBehaviorInstructionContext> BuildInstructionContextAsync(CancellationToken cancellationToken = default)
        => Invoke(async (client, token) => Map<AgentBehaviorInstructionContext>(await client.BuildInstructionContextAsync(Invocation, token).ConfigureAwait(false)), cancellationToken);
    public void SelectSessionContextProjection(AgentSessionPromptProjection projection)
        => Invoke((client, token) => client.SelectSessionContextAsync(new() { Invocation = Invocation, Projection = Map<Wire.AgentSessionPromptProjection>(projection) }, token)).AsTask().GetAwaiter().GetResult();
    public void ReportRunActivity(AgentRunActivityKind kind, string text)
        => Invoke((client, token) => client.ReportActivityAsync(new() { Invocation = Invocation, Kind = Enum.Parse<Wire.AgentRunActivityKind>(kind.ToString()), Text = text }, token)).AsTask().GetAwaiter().GetResult();
    public AgentRunBudgetState GetRunBudgetState()
        => Invoke(async (client, token) => Budget(await client.GetRunBudgetAsync(Invocation, token).ConfigureAwait(false))).AsTask().GetAwaiter().GetResult();
    public AgentRunBudgetState ChargeRunBudget(AgentRunBudgetCharge charge)
        => Invoke(async (client, token) => Budget(await client.ChargeRunBudgetAsync(new() { Invocation = Invocation, Charge = Map<Wire.AgentRunBudgetCharge>(charge) }, token).ConfigureAwait(false))).AsTask().GetAwaiter().GetResult();
    public async ValueTask AcknowledgePromptContextAsync(IReadOnlyList<AgentPromptContextReceiptBlock> blocks, CancellationToken cancellationToken)
        => _ = await Invoke((client, token) => client.AcknowledgePromptContextAsync(new() { Invocation = Invocation, Blocks = Map<Wire.AgentPromptContextReceiptBlock[]>(blocks) }, token), cancellationToken).ConfigureAwait(false);
    public void DiscardPromptContextAcknowledgment()
        => Invoke((client, token) => client.DiscardPromptContextAsync(Invocation, token)).AsTask().GetAwaiter().GetResult();

    public async ValueTask<IReadOnlyList<AgentRuntimeTool>> ListReadyToolsAsync(CancellationToken cancellationToken = default)
    {
        var descriptors = await Invoke(async (client, token) => Map<AgentToolDescriptor[]>(await client.ListReadyToolsAsync(Invocation, token).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
        return descriptors.Select(descriptor => new AgentRuntimeTool(descriptor,
            AIFunctionFactory.CreateDeclaration(descriptor.ToolId, descriptor.Description, ToolSchema(descriptor.ArgumentsJsonSchema)))).ToArray();
    }

    private static JsonElement ToolSchema(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false });
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    public AgentRunCheckpointRecord SaveCheckpoint(AgentRunStatus status, string? summary)
        => Invoke(async (client, token) => Map<AgentRunCheckpointRecord>(await client.SaveCheckpointAsync(new() { Invocation = Invocation,
            Status = Enum.Parse<Wire.AgentRunStatus>(status.ToString()), Summary = summary }, token).ConfigureAwait(false))).AsTask().GetAwaiter().GetResult();
    public void LogEvent(PackageLogLevel level, string eventName, string message, long? elapsedMilliseconds = null,
        IReadOnlyDictionary<string, object?>? attributes = null, Exception? exception = null)
        => Invoke((client, token) => client.LogEventAsync(new()
        {
            Invocation = Invocation, Level = Enum.Parse<Wire.PackageLogLevel>(level.ToString()), EventName = eventName, Message = message,
            ElapsedMilliseconds = elapsedMilliseconds, ExceptionType = exception?.GetType().FullName, ExceptionMessage = exception?.Message,
            Attributes = attributes?.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value), StringComparer.Ordinal),
        }, token)).AsTask().GetAwaiter().GetResult();
    public AgentTurnRecord UpsertAssistantTurn(AgentTurnRecord? assistantTurn, string content)
        => Invoke(async (client, token) => Map<AgentTurnRecord>(await client.UpsertAssistantTurnAsync(new() { Invocation = Invocation,
            AssistantTurn = Optional<Wire.AgentTurnRecord>(assistantTurn), Content = content }, token).ConfigureAwait(false))).AsTask().GetAwaiter().GetResult();
    public AgentTurnRecord CompleteAssistantTurn(AgentTurnRecord assistantTurn)
        => Invoke(async (client, token) => Map<AgentTurnRecord>(await client.CompleteAssistantTurnAsync(new() { Invocation = Invocation,
            AssistantTurn = Map<Wire.AgentTurnRecord>(assistantTurn) }, token).ConfigureAwait(false))).AsTask().GetAwaiter().GetResult();
    public async ValueTask PublishLifecycleEventAsync(AgentLifecycleEventKind kind, AgentRunStatus status, AgentTurnRecord? triggerTurn = null,
        AgentRunCheckpointRecord? checkpoint = null, bool isInterrupted = false, CancellationToken cancellationToken = default)
        => _ = await Invoke((client, token) => client.PublishLifecycleAsync(new()
        {
            Invocation = Invocation, Kind = Enum.Parse<Wire.AgentLifecycleEventKind>(kind.ToString()), Status = Enum.Parse<Wire.AgentRunStatus>(status.ToString()),
            TriggerTurn = Optional<Wire.AgentTurnRecord>(triggerTurn), Checkpoint = Optional<Wire.AgentRunCheckpointRecord>(checkpoint), IsInterrupted = isInterrupted,
        }, token), cancellationToken).ConfigureAwait(false);
    public ValueTask<AgentToolCallOutcome> InvokeToolAsync(AgentToolCallRequest toolCall, AgentTurnRecord? assistantTurn, CancellationToken cancellationToken = default)
        => Invoke(async (client, token) => Map<AgentToolCallOutcome>(await client.InvokeToolAsync(new() { Invocation = Invocation,
            ToolCall = Map<Wire.AgentToolCallRequest>(toolCall), AssistantTurn = Optional<Wire.AgentTurnRecord>(assistantTurn) }, token).ConfigureAwait(false)), cancellationToken, extended: true);
    public ValueTask<IReadOnlyList<AgentToolCallOutcome>> InvokeToolsAsync(IReadOnlyList<AgentToolCallRequest> toolCalls, AgentTurnRecord? assistantTurn, CancellationToken cancellationToken = default)
        => Invoke<IReadOnlyList<AgentToolCallOutcome>>(async (client, token) => Map<AgentToolCallOutcome[]>(await client.InvokeToolsAsync(new() { Invocation = Invocation,
            ToolCalls = Map<Wire.AgentToolCallRequest[]>(toolCalls), AssistantTurn = Optional<Wire.AgentTurnRecord>(assistantTurn) }, token).ConfigureAwait(false)), cancellationToken, extended: true);
    public ValueTask<AgentBehaviorLoopResult> RunDefaultLoopAsync(AgentBehaviorLoopContext context, CancellationToken cancellationToken = default)
        => Invoke(async (client, token) => Map<AgentBehaviorLoopResult>(await client.RunDefaultAsync(new() { Invocation = Invocation,
            Context = Map<Wire.AgentBehaviorLoopContext>(context) }, token).ConfigureAwait(false)), cancellationToken, extended: true);

    private async ValueTask<T> Invoke<T>(Func<Wire.RunControlClient, CancellationToken, ValueTask<T>> call, CancellationToken token = default, bool extended = false)
    {
        token.ThrowIfCancellationRequested();
        if (!_control.TryAcquire(out var lease)) throw Stale();
        using (lease)
        using (var invocation = CancellationTokenSource.CreateLinkedTokenSource(token, lease.RetirementToken))
            return await lease.Service.Invoke(call, invocation.Token, extended).ConfigureAwait(false);
    }

    private static SunderRpcException Stale() => new(new(SunderRpcErrorKind.StaleEndpoint, "agent.run-control.stale", "The Agent run-control provider activation is stale."));
}
