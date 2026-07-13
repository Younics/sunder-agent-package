namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentLifecycleObserverResult(
    [property: Obsolete("Active session continuity summaries are owned by Sunder Agent core. Lifecycle observers should store durable memory or contribute prompt context instead.")]
    string? WorkingSummary = null);
