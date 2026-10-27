using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>Accepts the exact transcript projection selected for the next provider request.</summary>
public interface IAgentSessionContextSelectionRuntime
{
    /// <summary>Selects the projection used by later prompt-context callbacks.</summary>
    void SelectSessionContextProjection(AgentSessionPromptProjection projection);
}

/// <summary>Publishes transient run activity to the host.</summary>
public interface IAgentRunActivitySink
{
    /// <summary>Reports the current bounded activity text.</summary>
    void ReportRunActivity(AgentRunActivityKind kind, string text);
}

/// <summary>Reads and atomically increments durable usage counters for one run generation. The budget-named contract is retained for compatibility; it does not limit task length.</summary>
public interface IAgentRunBudgetRuntime
{
    /// <summary>Gets the current durable usage counters.</summary>
    AgentRunBudgetState GetRunBudgetState();

    /// <summary>Atomically records usage and returns the resulting counters.</summary>
    AgentRunBudgetState ChargeRunBudget(AgentRunBudgetCharge charge);
}
