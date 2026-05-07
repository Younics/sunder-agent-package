using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed class SunderAgentToolInvoker(
    AgentBehaviorLoopContext context,
    IAgentBehaviorLoopRuntime host)
{
    private readonly AgentBehaviorLoopContext _context = context;
    private readonly IAgentBehaviorLoopRuntime _host = host;
    private AgentBehaviorLoopResult? _terminalResult;

    private int _toolBoundaryVersion;

    public int ToolBoundaryVersion => Volatile.Read(ref _toolBoundaryVersion);

    public AgentBehaviorLoopResult? TerminalResult => Volatile.Read(ref _terminalResult);

    public async ValueTask<object?> InvokeAsync(FunctionInvocationContext invocationContext, CancellationToken cancellationToken)
    {
        if (TerminalResult is not null)
        {
            invocationContext.Terminate = true;
            return "Tool execution skipped because a previous tool call stopped the run.";
        }

        var callContent = invocationContext.CallContent;
        var toolCall = new AgentToolCallRequest(
            callContent.CallId,
            callContent.Name,
            SerializeArguments(invocationContext.Arguments));

        var outcome = await _host.InvokeToolAsync(
            toolCall,
            assistantTurn: null,
            cancellationToken);

        if (outcome.Kind == AgentToolCallOutcomeKind.Executed)
        {
            Interlocked.Increment(ref _toolBoundaryVersion);
            return CreateToolResultContent(toolCall, outcome);
        }

        invocationContext.Terminate = true;
        SetTerminalResult(new AgentBehaviorLoopResult(
            outcome.Checkpoint ?? _context.RunningCheckpoint,
            outcome.Kind == AgentToolCallOutcomeKind.WaitingForApproval
                ? AgentBehaviorLoopCompletionKind.WaitingForApproval
                : AgentBehaviorLoopCompletionKind.Failed));
        return CreateToolResultContent(toolCall, outcome);
    }

    private void SetTerminalResult(AgentBehaviorLoopResult result)
        => Interlocked.CompareExchange(ref _terminalResult, result, null);

    private static string SerializeArguments(AIFunctionArguments? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            values[argument.Key] = argument.Value;
        }

        return JsonSerializer.Serialize(values);
    }

    private static string CreateToolResultContent(AgentToolCallRequest toolCall, AgentToolCallOutcome outcome)
    {
        if (outcome.Result is not null)
        {
            return string.IsNullOrWhiteSpace(outcome.Result.Content)
                ? outcome.Result.Summary
                : outcome.Result.Content;
        }

        return outcome.Kind switch
        {
            AgentToolCallOutcomeKind.WaitingForApproval => $"Tool '{toolCall.ToolId}' is waiting for user approval.",
            AgentToolCallOutcomeKind.Denied => $"Tool '{toolCall.ToolId}' was denied by policy.",
            _ => $"Tool '{toolCall.ToolId}' did not complete successfully.",
        };
    }
}

internal sealed class SunderAgentToolFunction(
    AgentRuntimeTool runtimeTool,
    SunderAgentToolInvoker invoker) : AIFunction
{
    private readonly AgentToolDescriptor _descriptor = runtimeTool.Descriptor;
    private readonly AIFunctionDeclaration _declaration = runtimeTool.Declaration;
    private readonly SunderAgentToolInvoker _invoker = invoker;

    public override string Name => _declaration.Name;

    public override string Description => _declaration.Description;

    public override JsonElement JsonSchema => _declaration.JsonSchema;

    public override JsonElement? ReturnJsonSchema => _declaration.ReturnJsonSchema;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
        => _invoker.InvokeAsync(new FunctionInvocationContext
        {
            Function = this,
            Arguments = arguments,
            CallContent = new FunctionCallContent(Guid.NewGuid().ToString("N"), _descriptor.ToolId, arguments),
        }, cancellationToken);
}
