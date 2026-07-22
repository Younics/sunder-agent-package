using System.Text.Json;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed record AgentRunBudgetLimits(
    TimeSpan MaxWallClock,
    int MaxProviderCycles,
    int MaxToolCalls,
    long MaxSubmittedContextTokens)
{
    public static AgentRunBudgetLimits Default { get; } = new(
        TimeSpan.FromMinutes(30),
        MaxProviderCycles: 64,
        MaxToolCalls: 128,
        MaxSubmittedContextTokens: 1_000_000);
}

internal enum AgentRunBudgetKind
{
    WallClock,
    ProviderCycles,
    ToolCalls,
    SubmittedContext,
}

internal sealed record AgentRunBudgetViolation(
    AgentRunBudgetKind Kind,
    string Summary,
    string VisibleMessage,
    long Consumed,
    long Limit);

internal sealed class AgentRunBudgetExceededException(AgentRunBudgetViolation violation) : Exception(violation.Summary)
{
    public AgentRunBudgetViolation Violation { get; } = violation;
}

internal sealed class AgentRunBudgetTracker
{
    private readonly AgentRunBudgetLimits _limits;
    private readonly Func<AgentRunBudgetCharge, AgentRunBudgetState>? _durableCharge;
    private readonly object _sync = new();
    private long _providerCycles;
    private long _toolCalls;
    private long _submittedContextTokens;

    public AgentRunBudgetTracker(
        AgentRunBudgetLimits limits,
        AgentRunBudgetState initialState = default,
        Func<AgentRunBudgetCharge, AgentRunBudgetState>? durableCharge = null)
    {
        if (limits.MaxWallClock <= TimeSpan.Zero
            || limits.MaxProviderCycles <= 0
            || limits.MaxToolCalls <= 0
            || limits.MaxSubmittedContextTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Every run budget limit must be positive.");
        }

        _limits = limits;
        _providerCycles = initialState.ProviderCycles;
        _toolCalls = initialState.ToolCalls;
        _submittedContextTokens = initialState.SubmittedContextTokens;
        _durableCharge = durableCharge;
    }

    public TimeSpan GetRemainingWallClock(DateTimeOffset runStartedAtUtc)
        => _limits.MaxWallClock - (DateTimeOffset.UtcNow - runStartedAtUtc);

    public AgentRunBudgetViolation CreateWallClockViolation()
        => new(
            AgentRunBudgetKind.WallClock,
            $"Agent run exceeded its {_limits.MaxWallClock.TotalMinutes:0.#}-minute wall-clock budget.",
            $"The run stopped after reaching its total wall-clock limit of {_limits.MaxWallClock.TotalMinutes:0.#} minutes.",
            (long)_limits.MaxWallClock.TotalMilliseconds,
            (long)_limits.MaxWallClock.TotalMilliseconds);

    public void ChargeProviderAttempt(
        IReadOnlyList<ChatMessage> promptMessages,
        int promptOverheadTokens)
    {
        var estimatedTokens = Math.Max(1, promptOverheadTokens) + EstimateMessageTokens(promptMessages);
        var state = ApplyCharge(new AgentRunBudgetCharge(
            ProviderCycles: 1,
            SubmittedContextTokens: estimatedTokens));
        var cycleCount = state.ProviderCycles;
        if (cycleCount > _limits.MaxProviderCycles)
        {
            throw new AgentRunBudgetExceededException(new AgentRunBudgetViolation(
                AgentRunBudgetKind.ProviderCycles,
                $"Agent run exceeded its {_limits.MaxProviderCycles}-cycle provider budget.",
                $"The run stopped after reaching its limit of {_limits.MaxProviderCycles} provider cycles, including retries.",
                cycleCount,
                _limits.MaxProviderCycles));
        }

        var total = state.SubmittedContextTokens;
        if (total > _limits.MaxSubmittedContextTokens)
        {
            throw new AgentRunBudgetExceededException(new AgentRunBudgetViolation(
                AgentRunBudgetKind.SubmittedContext,
                $"Agent run exceeded its {_limits.MaxSubmittedContextTokens:N0}-token cumulative context budget.",
                $"The run stopped after reaching its cumulative submitted-context limit of {_limits.MaxSubmittedContextTokens:N0} estimated tokens.",
                total,
                _limits.MaxSubmittedContextTokens));
        }
    }

    public void ChargeToolCalls(int count)
    {
        if (count <= 0)
        {
            return;
        }

        var total = ApplyCharge(new AgentRunBudgetCharge(ToolCalls: count)).ToolCalls;
        if (total > _limits.MaxToolCalls)
        {
            throw new AgentRunBudgetExceededException(new AgentRunBudgetViolation(
                AgentRunBudgetKind.ToolCalls,
                $"Agent run exceeded its {_limits.MaxToolCalls}-call tool budget.",
                $"The run stopped before executing a tool batch that would exceed the total limit of {_limits.MaxToolCalls} tool calls.",
                total,
                _limits.MaxToolCalls));
        }
    }

    private static long EstimateMessageTokens(IReadOnlyList<ChatMessage> messages)
    {
        long characters = messages.Count * 64L;
        long binaryBytes = 0;
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        characters = SaturatingAdd(characters, text.Text?.Length ?? 0);
                        break;
                    case FunctionCallContent call:
                        characters = SaturatingAdd(characters, call.Name?.Length ?? 0);
                        characters = SaturatingAdd(characters, SafeJsonLength(call.Arguments));
                        break;
                    case FunctionResultContent result:
                        characters = SaturatingAdd(characters, SafeJsonLength(result.Result));
                        break;
                    case DataContent data:
                        binaryBytes = SaturatingAdd(binaryBytes, data.Data.Length);
                        break;
                    default:
                        characters = SaturatingAdd(characters, content.ToString()?.Length ?? 0);
                        break;
                }
            }
        }

        return Math.Max(1, characters / 4L + binaryBytes / 3L);
    }

    private static int SafeJsonLength(object? value)
    {
        try
        {
            return value is null ? 0 : JsonSerializer.Serialize(value).Length;
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            return value?.ToString()?.Length ?? 0;
        }
    }

    private static long SaturatingAdd(long left, long right)
        => right > long.MaxValue - left ? long.MaxValue : left + right;

    private AgentRunBudgetState ApplyCharge(AgentRunBudgetCharge charge)
    {
        lock (_sync)
        {
            var state = _durableCharge?.Invoke(charge)
                        ?? new AgentRunBudgetState(
                            SaturatingAdd(_providerCycles, charge.ProviderCycles),
                            SaturatingAdd(_toolCalls, charge.ToolCalls),
                            SaturatingAdd(_submittedContextTokens, charge.SubmittedContextTokens));
            _providerCycles = state.ProviderCycles;
            _toolCalls = state.ToolCalls;
            _submittedContextTokens = state.SubmittedContextTokens;
            return state;
        }
    }
}

internal interface IAgentRunBudgetRuntime
{
    AgentRunBudgetState GetRunBudgetState();

    AgentRunBudgetState ChargeRunBudget(AgentRunBudgetCharge charge);
}
