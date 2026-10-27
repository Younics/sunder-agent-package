using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>A scheduled provider recovery, owned by the current run generation.</summary>
public sealed record AgentRunRetryState(
    [property: SunderValueConstraints(Minimum = "2", Maximum = "11")] int Attempt,
    DateTimeOffset RetryAtUtc,
    string Reason,
    bool IsContinuation);
