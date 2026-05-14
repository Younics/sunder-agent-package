namespace Sunder.Package.Agent.Contracts.Models;

public static class AgentToolResultErrorCodes
{
    public const string ChildWaitingForApproval = "child-waiting-for-approval";
    public const string SubagentRunFailed = "subagent-run-failed";
    public const string ShellNonZeroExit = "shell-nonzero-exit";
    public const string ShellTimeout = "shell-timeout";
    public const string ToolExecutionException = "tool-execution-exception";
}
