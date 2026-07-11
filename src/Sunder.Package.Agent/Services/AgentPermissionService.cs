using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentPermissionService(
    AgentLocalStore store,
    IPackageExtensionCatalog extensionCatalog)
{
    internal const string GenericMutationActionId = "agent.tool.mutate";
    internal const string GenericMutationBoundaryId = "provider-requested-mutation";

    private static readonly AgentPermissionActionDescriptor GenericMutationAction = new(
        GenericMutationActionId,
        "Run mutating tools",
        "Controls provider-requested tools that can change state but do not supply a more specific permission policy.",
        [
            new AgentPermissionBoundaryDescriptor(
                GenericMutationBoundaryId,
                "Provider-requested mutation",
                "Ask before running a mutating tool without a tool-specific permission policy.",
                AgentPermissionDecision.Ask),
        ]);

    private readonly AgentLocalStore _store = store;
    private readonly IPackageExtensionCatalog _extensionCatalog = extensionCatalog;

    public AgentSessionPermissionState GetSessionState(Guid sessionId)
        => _store.GetSessionPermissionState(sessionId);

    public void SetSessionUnrestrictedMode(Guid sessionId, bool isEnabled)
        => _store.SetSessionUnrestrictedMode(sessionId, isEnabled);

    public IReadOnlyList<AgentPermissionActionDescriptor> ListActions()
        => _extensionCatalog.GetExtensions(PackageExtensionPoints.PermissionSurfaces)
            .SelectMany(surface => surface.ListActions())
            .Append(GenericMutationAction)
            .GroupBy(action => action.ActionId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(action => action.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<AgentPermissionOverride> ListOverrides()
        => _store.ListPermissionOverrides();

    public void SaveOverride(string actionId, string boundaryId, AgentPermissionDecision decision)
        => _store.SavePermissionOverride(new AgentPermissionOverride(actionId, boundaryId, decision, DateTimeOffset.UtcNow));

    public void DeleteOverride(string actionId, string boundaryId)
        => _store.DeletePermissionOverride(actionId, boundaryId);

    public AgentPendingPermissionRequestRecord SavePendingRequest(AgentPendingPermissionRequestRecord request)
        => _store.SavePendingPermissionRequest(request);

    internal AgentPendingPermissionRequestRecord? SavePendingRequestAndSuspendRun(
        AgentPendingPermissionRequestRecord request,
        long expectedEpoch)
        => _store.SavePendingPermissionRequestAndSuspendRun(request, expectedEpoch);

    internal AgentPendingPermissionRequestRecord? SavePendingRequestAndSuspendRun(
        AgentPendingPermissionRequestRecord request,
        AgentDurableRunLease lease)
    {
        lock (lease.SyncRoot)
        {
            var persisted = _store.SavePendingPermissionRequestAndSuspendRun(
                request,
                lease.Epoch);
            if (persisted is not null)
            {
                lease.AdvanceTo(lease.Epoch + 1);
            }

            return persisted;
        }
    }

    public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingRequests(Guid sessionId)
        => _store.ListPendingPermissionRequests(sessionId);

    public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingRequestsForSessionTree(Guid sessionId)
        => _store.ListPendingPermissionRequestsForSessionTree(sessionId);

    public void SaveSessionApproval(Guid sessionId, string actionId, string boundaryId)
    {
        if (string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(boundaryId))
        {
            return;
        }

        _store.SaveSessionPermissionApproval(new AgentSessionPermissionApproval(
            Guid.NewGuid().ToString("N"),
            sessionId,
            actionId.Trim(),
            AgentPermissionMatcherKind.ActionId,
            boundaryId.Trim(),
            DateTimeOffset.UtcNow));
    }

    public AgentPendingPermissionRequestRecord? GetPendingRequest(Guid sessionId, string requestId)
        => _store.GetPendingPermissionRequest(sessionId, requestId);

    internal AgentPendingPermissionRequestRecord? GetRequest(Guid sessionId, string requestId)
        => _store.GetPermissionRequest(sessionId, requestId);

    internal AgentPendingPermissionClaimResult TryClaimPendingRequest(Guid sessionId, string requestId)
        => _store.TryClaimPendingPermissionRequest(sessionId, requestId);

    internal AgentPendingPermissionDecisionResult TryDenyPendingRequest(
        Guid sessionId,
        string requestId,
        string summary)
        => _store.TryDenyPendingPermissionRequest(sessionId, requestId, summary);

    internal bool CompleteClaimedRequest(
        AgentPendingPermissionRequestRecord request,
        AgentPendingPermissionStatus status,
        string summary)
        => !string.IsNullOrWhiteSpace(request.ClaimToken)
           && _store.CompleteClaimedPermissionRequest(
               request.SessionId,
               request.RequestId,
               request.ClaimToken,
               status,
               summary);

    internal AgentRunCheckpointRecord? ResumeClaimedRequest(
        AgentPendingPermissionRequestRecord request,
        long expectedEpoch)
        => _store.ResumeClaimedPermissionRequest(request, expectedEpoch);

    internal bool MarkExecutionStarted(AgentPendingPermissionRequestRecord request)
        => !string.IsNullOrWhiteSpace(request.ClaimToken)
           && _store.MarkClaimedPermissionExecutionStarted(
               request.SessionId,
               request.RequestId,
               request.ClaimToken);

    internal AgentRunCheckpointRecord? FinalizeClaimedRequest(
        AgentPendingPermissionRequestRecord request,
        AgentPendingPermissionStatus status,
        AgentRunStatus runStatus,
        string summary)
        => _store.FinalizeClaimedPermissionRequest(request, status, runStatus, summary);

    internal bool ExpireActiveRequest(Guid sessionId, string requestId, string summary)
        => _store.ExpireActivePermissionRequest(sessionId, requestId, summary);

    public void DeletePendingRequest(Guid sessionId, string requestId)
        => _store.DeletePendingPermissionRequest(sessionId, requestId);

    public AgentPermissionEvaluation Evaluate(Guid? sessionId, AgentPermissionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ActionId))
        {
            return new AgentPermissionEvaluation(AgentPermissionDecision.Deny, "Permission request action id is missing.");
        }

        var boundaryId = string.IsNullOrWhiteSpace(request.BoundaryId)
            ? AgentPermissionBoundaryIds.Unknown
            : request.BoundaryId;

        var action = ListActions().FirstOrDefault(action => string.Equals(action.ActionId, request.ActionId, StringComparison.OrdinalIgnoreCase));
        var boundary = action?.Boundaries.FirstOrDefault(item => string.Equals(item.BoundaryId, boundaryId, StringComparison.OrdinalIgnoreCase));
        if (action is null || boundary is null)
        {
            return new AgentPermissionEvaluation(AgentPermissionDecision.Ask, $"Unknown permission boundary '{request.ActionId}:{boundaryId}'.");
        }

        var permissionOverride = _store.ListPermissionOverrides()
            .FirstOrDefault(item => string.Equals(item.ActionId, request.ActionId, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(item.BoundaryId, boundaryId, StringComparison.OrdinalIgnoreCase));
        var decision = permissionOverride?.Decision ?? boundary.DefaultDecision;

        if (decision == AgentPermissionDecision.Ask && sessionId is { } resolvedSessionId)
        {
            if (FindSessionApprovalInHierarchy(resolvedSessionId, request, boundaryId) is not null)
            {
                return new AgentPermissionEvaluation(AgentPermissionDecision.Allow, "Allowed by session-scoped approval.", permissionOverride);
            }

            var unrestrictedSessionId = FindUnrestrictedSessionInHierarchy(resolvedSessionId);
            if (unrestrictedSessionId is not null)
            {
                var unrestrictedReason = unrestrictedSessionId == resolvedSessionId
                    ? "Allowed by session Unrestricted Mode."
                    : "Allowed by inherited Unrestricted Mode from parent session.";
                return new AgentPermissionEvaluation(AgentPermissionDecision.Allow, unrestrictedReason, permissionOverride);
            }
        }

        var reason = permissionOverride is null
            ? $"Using default decision for '{request.ActionId}' in '{boundaryId}'."
            : $"Using configured decision for '{request.ActionId}' in '{boundaryId}'.";
        return new AgentPermissionEvaluation(decision, reason, permissionOverride);
    }

    private AgentSessionPermissionApproval? FindSessionApprovalInHierarchy(Guid sessionId, AgentPermissionRequest request, string boundaryId)
    {
        foreach (var id in EnumerateSessionHierarchy(sessionId))
        {
            var approval = _store.ListSessionPermissionApprovals(id)
                .FirstOrDefault(item => string.Equals(item.ActionId, request.ActionId, StringComparison.OrdinalIgnoreCase)
                                        && IsApprovalMatch(item, request, boundaryId));
            if (approval is not null)
            {
                return approval;
            }
        }

        return null;
    }

    private Guid? FindUnrestrictedSessionInHierarchy(Guid sessionId)
    {
        foreach (var id in EnumerateSessionHierarchy(sessionId))
        {
            if (_store.GetSessionPermissionState(id).IsUnrestrictedModeEnabled)
            {
                return id;
            }
        }

        return null;
    }

    private IEnumerable<Guid> EnumerateSessionHierarchy(Guid sessionId)
    {
        var visited = new HashSet<Guid>();
        Guid? currentSessionId = sessionId;

        while (currentSessionId is { } id && visited.Add(id))
        {
            yield return id;

            var session = _store.GetSession(id);
            currentSessionId = session?.ParentSessionId
                               ?? (session?.RootSessionId is { } rootSessionId && rootSessionId != id ? rootSessionId : null);
        }
    }

    private static bool IsApprovalMatch(AgentSessionPermissionApproval approval, AgentPermissionRequest request, string boundaryId)
        => approval.MatcherKind switch
        {
            AgentPermissionMatcherKind.ActionId => string.Equals(approval.Pattern, boundaryId, StringComparison.OrdinalIgnoreCase),
            AgentPermissionMatcherKind.ToolId => !string.IsNullOrWhiteSpace(request.ToolId) && string.Equals(approval.Pattern, request.ToolId, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
}
