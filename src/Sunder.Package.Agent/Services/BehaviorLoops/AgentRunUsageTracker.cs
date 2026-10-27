using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

// The public BudgetState/Charge contracts retain their names for compatibility.
// These are usage counters, never a limit on productive work.
internal sealed class AgentRunUsageTracker
{
    private readonly Func<AgentRunBudgetCharge, CancellationToken, ValueTask<AgentRunBudgetState>>? _durableCharge;
    private readonly object _sync = new();
    private long _providerCycles;
    private long _toolCalls;
    private long _submittedContextTokens;

    public AgentRunUsageTracker(
        AgentRunBudgetState initialState = default,
        Func<AgentRunBudgetCharge, CancellationToken, ValueTask<AgentRunBudgetState>>? durableCharge = null)
    {
        _providerCycles = initialState.ProviderCycles;
        _toolCalls = initialState.ToolCalls;
        _submittedContextTokens = initialState.SubmittedContextTokens;
        _durableCharge = durableCharge;
    }

    public ValueTask<AgentRunBudgetState> ChargeProviderAttemptAsync(long estimatedSubmittedContextTokens, CancellationToken cancellationToken = default)
    {
        if (estimatedSubmittedContextTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedSubmittedContextTokens));
        }

        return ApplyChargeAsync(new AgentRunBudgetCharge(
            ProviderCycles: 1,
            SubmittedContextTokens: estimatedSubmittedContextTokens), cancellationToken);
    }

    public ValueTask<AgentRunBudgetState> ChargeToolCallsAsync(int count, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return ApplyChargeAsync(new AgentRunBudgetCharge(ToolCalls: count), cancellationToken);
    }

    private static long SaturatingAdd(long left, long right)
        => right > long.MaxValue - left ? long.MaxValue : left + right;

    private ValueTask<AgentRunBudgetState> ApplyChargeAsync(AgentRunBudgetCharge charge, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Durable accounting owns its atomic counters. Never hold the local
        // fallback lock while an asynchronous run-control operation is pending.
        if (_durableCharge is { } durableCharge) return durableCharge(charge, cancellationToken);
        lock (_sync)
        {
            var state = new AgentRunBudgetState(
                            SaturatingAdd(_providerCycles, charge.ProviderCycles),
                            SaturatingAdd(_toolCalls, charge.ToolCalls),
                            SaturatingAdd(_submittedContextTokens, charge.SubmittedContextTokens));
            _providerCycles = state.ProviderCycles;
            _toolCalls = state.ToolCalls;
            _submittedContextTokens = state.SubmittedContextTokens;
            return ValueTask.FromResult(state);
        }
    }
}
