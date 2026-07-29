namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines cross-package error codes that carry runtime control or common tool failure semantics.
/// </summary>
public static class AgentToolResultErrorCodes
{
    /// <summary>The invocation's transient outside-scope authority is unavailable and requires a new approval.</summary>
    public const string PermissionReapprovalRequired = "permission-reapproval-required";
    /// <summary>Signals that a child agent run suspended for approval and the parent must wait rather than treat the tool as complete.</summary>
    public const string ChildWaitingForApproval = "child-waiting-for-approval";

    /// <summary>Indicates that a delegated subagent run reached a terminal failure.</summary>
    public const string SubagentRunFailed = "subagent-run-failed";

    /// <summary>Indicates that a shell command completed with a nonzero exit code.</summary>
    public const string ShellNonZeroExit = "shell-nonzero-exit";

    /// <summary>Indicates that a shell command exceeded its target-enforced timeout.</summary>
    public const string ShellTimeout = "shell-timeout";

    /// <summary>Indicates that the host isolated an unexpected tool exception or non-caller cancellation as an error result.</summary>
    public const string ToolExecutionException = "tool-execution-exception";

    /// <summary>Indicates that the exact package activation selected for a tool callback retired before the callback completed.</summary>
    public const string PackageUnavailable = "package-unavailable";
}
