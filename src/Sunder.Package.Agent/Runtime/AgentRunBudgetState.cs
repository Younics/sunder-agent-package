namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Contains durable usage counters for one Agent run generation. These counters are not execution limits.</summary>
public readonly record struct AgentRunBudgetState(
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")]
    long ProviderCycles,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")]
    long ToolCalls,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")]
    long SubmittedContextTokens);

/// <summary>Describes an atomic increment to durable Agent run counters.</summary>
public readonly record struct AgentRunBudgetCharge(
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")]
    long ProviderCycles = 0,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")]
    long ToolCalls = 0,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")]
    long SubmittedContextTokens = 0);
