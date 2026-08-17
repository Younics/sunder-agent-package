using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.Runtime;

internal sealed class AgentPermissionCommandHandler(
    AgentPermissionService permissions,
    AgentRuntimeChangeHub changes)
{
    public ValueTask<AgentPermissionProjection> HandleAsync(
        AgentPermissionCommand request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (request.Kind)
        {
            case AgentPermissionCommandKind.Read:
                break;
            case AgentPermissionCommandKind.SetUnrestricted:
                permissions.SetSessionUnrestrictedMode(RequireSession(request), request.IsEnabled == true);
                break;
            case AgentPermissionCommandKind.SaveOverride:
                permissions.SaveOverride(Require(request.ActionId), Require(request.BoundaryId),
                    request.Decision ?? throw new AgentOwnerOperationException("Permission decision is required."));
                break;
            case AgentPermissionCommandKind.DeleteOverride:
                permissions.DeleteOverride(Require(request.ActionId), Require(request.BoundaryId));
                break;
            default:
                throw new AgentOwnerOperationException("Unknown permission command.");
        }
        if (request.Kind != AgentPermissionCommandKind.Read)
        {
            changes.NotifyPermissionChanged(request.SessionId);
        }
        var sessionId = request.SessionId;
        var includeGlobalRules = sessionId is null;
        return ValueTask.FromResult(new AgentPermissionProjection(
            changes.Revision,
            sessionId is { } id ? permissions.GetSessionState(id) : null,
            includeGlobalRules ? permissions.ListActions() : [],
            includeGlobalRules ? permissions.ListOverrides() : [],
            sessionId is { } pendingId ? permissions.ListPendingRequestsForSessionTree(pendingId) : []));
    }

    private static Guid RequireSession(AgentPermissionCommand request)
        => request.SessionId ?? throw new AgentOwnerOperationException("Session id is required.");

    private static string Require(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new AgentOwnerOperationException("Permission identifier is required.")
            : value;
}
