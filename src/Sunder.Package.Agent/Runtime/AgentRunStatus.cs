using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes the durable lifecycle state recorded for a run revision.
/// </summary>
/// <remarks>
/// Interrupted, stopped, completed, and failed are terminal for one run revision. Waiting for approval is resumable.
/// A session can start a newer run revision after any terminal state; callers must not compare statuses from different
/// revisions as if they were one transition sequence.
/// </remarks>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum AgentRunStatus
{
    /// <summary>No provider execution is active for the run revision, typically before its running transition.</summary>
    [JsonStringEnumMemberName("idle")] Idle = 0,

    /// <summary>The run may prepare prompts, call a provider, stream transcript content, and invoke tools.</summary>
    [JsonStringEnumMemberName("running")] Running = 1,

    /// <summary>Execution ended without normal completion because it was canceled, superseded, recovered, or transiently disrupted.</summary>
    [JsonStringEnumMemberName("interrupted")] Interrupted = 2,

    /// <summary>Execution ended in response to an explicit stop or deletion operation.</summary>
    [JsonStringEnumMemberName("stopped")] Stopped = 3,

    /// <summary>Execution reached its intended successful terminal result.</summary>
    [JsonStringEnumMemberName("completed")] Completed = 4,

    /// <summary>Execution reached a terminal error; the checkpoint summary may contain diagnostic detail.</summary>
    [JsonStringEnumMemberName("failed")] Failed = 5,

    /// <summary>Execution is durably suspended pending a user permission decision and may resume within the same run revision.</summary>
    [JsonStringEnumMemberName("waiting-for-approval")] WaitingForApproval = 6,
}
