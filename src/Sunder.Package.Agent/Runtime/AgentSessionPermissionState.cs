namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Captures the directly persisted permission mode for one session.
/// </summary>
/// <remarks>
/// Permission evaluation may also inherit unrestricted mode or approvals from ancestor sessions. This snapshot alone
/// therefore does not describe the effective decision for a particular tool request.
/// </remarks>
/// <param name="SessionId">The session whose direct permission setting was read.</param>
/// <param name="IsUnrestrictedModeEnabled">Whether ask-by-default permission boundaries are bypassed for this session, subject to hard denials and host security checks.</param>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentSessionPermissionState(
    Guid SessionId,
    bool IsUnrestrictedModeEnabled);
