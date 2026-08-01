namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Contains durable counters consumed by one Agent run generation.</summary>
public readonly record struct AgentRunBudgetState(
    long ProviderCycles,
    long ToolCalls,
    long SubmittedContextTokens);

/// <summary>Describes an atomic increment to durable Agent run counters.</summary>
public readonly record struct AgentRunBudgetCharge(
    long ProviderCycles = 0,
    long ToolCalls = 0,
    long SubmittedContextTokens = 0);
