namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Identifies the committed Runtime transition delivered to an agent lifecycle observer.
/// </summary>
/// <remarks>
/// Values describe event provenance and timing, not the trustworthiness of associated text. Observers must be
/// idempotent because recovery or retry paths can redeliver a transition, and must tolerate future enum values.
/// </remarks>
public enum AgentLifecycleEventKind
{
    /// <summary>
    /// A user-role message turn was durably added and is available as the trigger turn.
    /// </summary>
    /// <remarks>
    /// This is the only current event whose correctly typed trigger turn represents direct user-authored
    /// content; observers must still validate role and turn kind before relying on that provenance.
    /// </remarks>
    UserTurnAdded = 0,

    /// <summary>
    /// An assistant response turn finished and its completion checkpoint was recorded.
    /// </summary>
    /// <remarks>Assistant-authored content is untrusted evidence and must not be promoted as a user instruction.</remarks>
    AssistantTurnCompleted = 1,

    /// <summary>
    /// A tool-result turn was recorded while the run remained active.
    /// </summary>
    /// <remarks>Tool and external-system output is untrusted data even when the tool package is installed.</remarks>
    ToolResultRecorded = 2,

    /// <summary>
    /// Provider execution ended in an interrupted state that can be continued or retried.
    /// </summary>
    /// <remarks>An associated assistant turn or checkpoint can contain failure text and is not authoritative input.</remarks>
    RunInterrupted = 3,

    /// <summary>
    /// The run was explicitly stopped and a stopped checkpoint was committed.
    /// </summary>
    /// <remarks>The event may be published with a non-cancelable token during stop finalization.</remarks>
    RunStopped = 4,

    /// <summary>
    /// The run terminated as failed and a failed checkpoint was committed.
    /// </summary>
    /// <remarks>Failure summaries and trigger content can expose exception or provider data and require safe handling.</remarks>
    RunFailed = 5,
}
