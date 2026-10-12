using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Classifies why a behavior-loop invocation returned control to the host.
/// </summary>
public enum AgentBehaviorLoopCompletionKind
{
    /// <summary>The loop produced its intended successful terminal result.</summary>
    [JsonStringEnumMemberName("completed")] Completed = 0,

    /// <summary>The loop terminated because provider, tool, budget, persistence, or extension work failed.</summary>
    [JsonStringEnumMemberName("failed")] Failed = 1,

    /// <summary>The loop durably suspended pending user approval and can resume the same run revision.</summary>
    [JsonStringEnumMemberName("waiting-for-approval")] WaitingForApproval = 2,

    /// <summary>The loop observed an explicit stop and returned without further mutations.</summary>
    [JsonStringEnumMemberName("stopped")] Stopped = 3,

    /// <summary>The loop lost current-run ownership or observed cancellation/interruption.</summary>
    [JsonStringEnumMemberName("interrupted")] Interrupted = 4,
}
