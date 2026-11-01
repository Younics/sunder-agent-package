using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts;

/// <summary>
/// Exposes an immutable session snapshot to Agent extensions.
/// </summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public interface IAgentSessionContext
{
    /// <summary>Gets the stable session identifier.</summary>
    Guid SessionId { get; }

    /// <summary>Gets the opaque identifier of the profile selected for the session.</summary>
    string ProfileId { get; }

    /// <summary>Gets the profile display name captured for this request or event.</summary>
    string ProfileDisplayName { get; }

    /// <summary>Gets the session title captured for this request or event.</summary>
    string SessionTitle { get; }

    /// <summary>Gets the projected session lifecycle state at snapshot creation time.</summary>
    AgentSessionState SessionState { get; }

}

/// <summary>
/// Implements <see cref="IAgentSessionContext"/> as an immutable value snapshot for extension requests and events.
/// </summary>
/// <param name="SessionId">The stable session identifier.</param>
/// <param name="ProfileId">The selected profile identifier.</param>
/// <param name="ProfileDisplayName">The selected profile's display name at snapshot creation time.</param>
/// <param name="SessionTitle">The persisted session title at snapshot creation time.</param>
/// <param name="SessionState">The projected lifecycle state at snapshot creation time.</param>
public sealed record AgentSessionContextRecord(
    Guid SessionId,
    string ProfileId,
    string ProfileDisplayName,
    string SessionTitle,
    AgentSessionState SessionState) : IAgentSessionContext;
