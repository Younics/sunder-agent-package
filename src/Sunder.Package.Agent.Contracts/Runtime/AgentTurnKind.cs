namespace Sunder.Package.Agent.Contracts.Models;

public enum AgentTurnKind
{
    Message = 0,
    ToolCall = 1,
    ToolResult = 2,
}
