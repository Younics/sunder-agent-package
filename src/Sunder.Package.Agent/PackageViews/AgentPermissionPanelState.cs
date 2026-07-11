using System.Collections.ObjectModel;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

internal sealed class AgentPermissionPanelState(
    AgentPermissionService permissionService,
    AgentRunCoordinator runCoordinator)
{
    private Guid? _sessionId;

    public ObservableCollection<AgentPendingPermissionRequestRecord> Requests { get; } = [];

    public IReadOnlyList<TranscriptPermissionProjection> RowPresentations { get; private set; } = [];

    public bool HasRequests => Requests.Count > 0;

    public bool IsUnrestrictedModeEnabled { get; private set; }

    public void LoadSession(Guid? sessionId)
    {
        _sessionId = sessionId;
        IsUnrestrictedModeEnabled = sessionId is { } id
            && permissionService.GetSessionState(id).IsUnrestrictedModeEnabled;
        Reload();
    }

    public string SetUnrestrictedMode(bool value)
    {
        IsUnrestrictedModeEnabled = value;
        if (_sessionId is not { } sessionId)
        {
            return string.Empty;
        }

        permissionService.SetSessionUnrestrictedMode(sessionId, value);
        return value
            ? "Unrestricted Mode is enabled for this session. Ask-style approvals are auto-approved, but hard constraints still apply."
            : "Unrestricted Mode is disabled for this session.";
    }

    public void Reload()
    {
        Requests.Clear();
        if (_sessionId is { } sessionId)
        {
            foreach (var request in permissionService.ListPendingRequestsForSessionTree(sessionId))
            {
                Requests.Add(request);
            }
        }

        RowPresentations = Requests
            .Select(request => TranscriptRowProjector<object>.DescribePermission(
                request.RequestId,
                request.Summary,
                request.Command,
                request.Path))
            .ToArray();
    }

    public async Task<string> ApproveAsync(
        AgentPendingPermissionRequestRecord request,
        bool approveForSession)
    {
        if (approveForSession)
        {
            permissionService.SaveSessionApproval(
                request.SessionId,
                request.ActionId,
                request.BoundaryId);
        }

        var checkpoint = await runCoordinator.ApprovePendingPermissionAsync(
            request.SessionId,
            request.RequestId);
        Reload();
        return checkpoint?.Summary ?? "Permission request was no longer pending.";
    }

    public async Task<string> DenyAsync(AgentPendingPermissionRequestRecord request)
    {
        await runCoordinator.DenyPendingPermissionAsync(
            request.SessionId,
            request.RequestId).ConfigureAwait(false);
        Reload();
        return "Permission request denied.";
    }
}
