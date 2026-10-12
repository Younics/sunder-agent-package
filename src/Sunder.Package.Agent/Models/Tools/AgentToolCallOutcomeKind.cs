namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines how processing a tool call affects the durable provider loop.
/// </summary>
public enum AgentToolCallOutcomeKind
{
    /// <summary>The call has a recorded result; that result may itself report a recoverable tool error.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("executed")] Executed = 0,

    /// <summary>The run is durably suspended until user approval or a dependent child run can continue.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("waiting-for-approval")] WaitingForApproval = 1,

    /// <summary>A permission or security check prevented execution and the run cannot continue normally.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("denied")] Denied = 2,

    /// <summary>Tool-call processing failed before a normal executed result could be recorded.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("failed")] Failed = 3,
}
