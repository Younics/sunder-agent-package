using System.Collections.ObjectModel;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

internal sealed class AgentPermissionPanelState(
    IAgentPermissionGateway permissionService,
    IAgentRunGateway runCoordinator)
{
    private Guid? _sessionId;

    public ObservableCollection<AgentPendingPermissionRequestRecord> Requests { get; } = [];

    public IReadOnlyList<TranscriptPermissionProjection> RowPresentations { get; private set; } = [];

    public bool HasRequests => Requests.Count > 0;

    public bool IsUnrestrictedModeEnabled { get; private set; }

    public void ApplySnapshot(
        Guid? sessionId,
        AgentSessionPermissionState? sessionState,
        IReadOnlyList<AgentPendingPermissionRequestRecord> requests)
    {
        _sessionId = sessionId;
        IsUnrestrictedModeEnabled = sessionState?.IsUnrestrictedModeEnabled == true;
        Requests.Clear();
        foreach (var request in requests)
        {
            Requests.Add(request);
        }

        UpdateRowPresentations();
    }

    public void SetUnrestrictedModeValue(bool value)
    {
        IsUnrestrictedModeEnabled = value;
    }

    public static string DescribeUnrestrictedMode(bool value)
        => value
            ? "Unrestricted Mode is enabled for this session. Ask-style approvals are auto-approved, but hard constraints still apply: Docker Files access remains exact-resource bound, and shell commands do not gain scoped-instruction enforcement."
            : "Unrestricted Mode is disabled for this session.";

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

        UpdateRowPresentations();
    }

    private void UpdateRowPresentations()
    {
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
        if (runCoordinator is IAgentChatRunGateway chatRuns)
        {
            var chatCheckpoint = await chatRuns.ApprovePendingPermissionAsync(
                request.SessionId,
                request.RequestId,
                approveForSession).ConfigureAwait(false);
            return chatCheckpoint?.Summary ?? "Permission request was no longer pending.";
        }

        if (approveForSession)
        {
            permissionService.SaveSessionApproval(request.SessionId, request.ActionId, request.BoundaryId);
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
        if (permissionService is not IAgentChatPermissionCommandGateway)
        {
            Reload();
        }
        return "Permission request denied.";
    }
}
