using System.Collections.ObjectModel;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.PackageViews;

internal sealed class AgentPermissionPanelState(
    IAgentPermissionGateway permissionService,
    IAgentRunGateway runCoordinator,
    IPresentationDispatcher uiDispatcher)
{
    private Guid? _sessionId;
    private readonly Dictionary<(Guid SessionId, string RequestId), Decision> _decisions = [];

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
        ReplaceRequests(requests);
    }

    private void ReplaceRequests(IReadOnlyList<AgentPendingPermissionRequestRecord> requests)
    {
        var pending = requests.Select(request => (request.SessionId, request.RequestId)).ToHashSet();
        foreach (var (key, decision) in _decisions.ToArray())
        {
            if (decision.Accepted && decision.DisplaySessionId == _sessionId && !pending.Contains(key))
                _decisions.Remove(key);
        }
        Requests.Clear();
        foreach (var request in requests)
        {
            if (!_decisions.ContainsKey((request.SessionId, request.RequestId))) Requests.Add(request);
        }

        UpdateRowPresentations();
    }

    public Decision? TryBeginDecision(AgentPendingPermissionRequestRecord request)
    {
        if (_sessionId is not { } sessionId || string.IsNullOrWhiteSpace(request.RequestId)) return null;
        var key = (request.SessionId, request.RequestId);
        var pending = Requests.FirstOrDefault(item => (item.SessionId, item.RequestId) == key);
        if (pending is null || _decisions.ContainsKey(key)) return null;
        var decision = new Decision(sessionId, pending);
        _decisions.Add(key, decision);
        Requests.Remove(pending);
        UpdateRowPresentations();
        return decision;
    }

    public bool IsCurrentDecision(Decision decision)
        => _decisions.TryGetValue((decision.Request.SessionId, decision.Request.RequestId), out var current)
           && ReferenceEquals(current, decision);

    public void AcceptDecision(Decision decision)
    {
        if (IsCurrentDecision(decision)) decision.Accepted = true;
    }

    public void ReleaseFailedDecision(Decision decision)
    {
        if (IsCurrentDecision(decision) && !decision.Accepted)
            _decisions.Remove((decision.Request.SessionId, decision.Request.RequestId));
    }

    public void ResetDecisions() => _decisions.Clear();

    internal sealed class Decision(Guid displaySessionId, AgentPendingPermissionRequestRecord request)
    {
        public Guid DisplaySessionId { get; } = displaySessionId;
        public AgentPendingPermissionRequestRecord Request { get; } = request;
        public bool Accepted { get; set; }
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
        ReplaceRequests(_sessionId is { } sessionId
            ? permissionService.ListPendingRequestsForSessionTree(sessionId) : []);
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
        bool approveForSession,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await ((IAgentChatRunGateway)runCoordinator).ApprovePendingPermissionAsync(
            request.SessionId,
            request.RequestId,
            approveForSession,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (permissionService is not IAgentChatPermissionCommandGateway)
        {
            await uiDispatcher.InvokeAsync(Reload).ConfigureAwait(false);
        }
        return checkpoint?.Summary ?? "Permission request was no longer pending.";
    }

    public async Task<string> DenyAsync(AgentPendingPermissionRequestRecord request, CancellationToken cancellationToken = default)
    {
        await runCoordinator.DenyPendingPermissionAsync(
            request.SessionId,
            request.RequestId,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (permissionService is not IAgentChatPermissionCommandGateway)
        {
            await uiDispatcher.InvokeAsync(Reload).ConfigureAwait(false);
        }
        return "Permission request denied.";
    }
}
