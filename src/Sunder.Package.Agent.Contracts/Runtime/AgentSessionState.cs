namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes the session-level projection of its newest applicable run state.
/// </summary>
/// <remarks>
/// These values are not permanently terminal for the conversation: queueing a newer run revision can return an
/// interrupted, stopped, completed, or failed session to <see cref="Active"/>.
/// </remarks>
public enum AgentSessionState
{
    /// <summary>The session can accept work and its latest run is preparing, running, idle, or waiting for approval.</summary>
    Active = 0,

    /// <summary>The latest applicable run ended through cancellation, supersession, recovery, or another interruption.</summary>
    Interrupted = 1,

    /// <summary>The latest applicable run was explicitly stopped.</summary>
    Stopped = 2,

    /// <summary>The latest applicable run completed successfully.</summary>
    Completed = 3,

    /// <summary>The latest applicable run failed.</summary>
    Failed = 5,
}
