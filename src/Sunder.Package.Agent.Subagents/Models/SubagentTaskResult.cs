using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Subagents.Models;

internal enum SubagentTaskResultState
{
    Completed = 0,
    Waiting = 1,
    Failed = 2,
    Stopped = 3,
    Interrupted = 4,
}

internal sealed record SubagentTaskResult(
    SubagentTaskResultState State,
    AgentToolResult ToolResult);
