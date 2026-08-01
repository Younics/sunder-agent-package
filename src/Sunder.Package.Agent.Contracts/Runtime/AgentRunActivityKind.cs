namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Identifies transient activity reported while an Agent run is active.</summary>
public enum AgentRunActivityKind
{
    Thinking = 0,
    Reasoning = 1,
    Tool = 2,
    Processing = 3,
}
