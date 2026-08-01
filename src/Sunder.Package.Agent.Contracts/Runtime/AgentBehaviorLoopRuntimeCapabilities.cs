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

/// <summary>Reads and atomically charges the durable budget for one run generation.</summary>
public interface IAgentRunBudgetRuntime
{
    /// <summary>Gets the current durable budget counters.</summary>
    AgentRunBudgetState GetRunBudgetState();

    /// <summary>Atomically applies a budget charge and returns the resulting counters.</summary>
    AgentRunBudgetState ChargeRunBudget(AgentRunBudgetCharge charge);
}
