namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Classifies why a behavior-loop invocation returned control to the host.
/// </summary>
public enum AgentBehaviorLoopCompletionKind
{
    /// <summary>The loop produced its intended successful terminal result.</summary>
    Completed = 0,

    /// <summary>The loop terminated because provider, tool, budget, persistence, or extension work failed.</summary>
    Failed = 1,

    /// <summary>The loop durably suspended pending user approval and can resume the same run revision.</summary>
    WaitingForApproval = 2,

    /// <summary>The loop observed an explicit stop and returned without further mutations.</summary>
    Stopped = 3,

    /// <summary>The loop lost current-run ownership or observed cancellation/interruption.</summary>
    Interrupted = 4,
}
