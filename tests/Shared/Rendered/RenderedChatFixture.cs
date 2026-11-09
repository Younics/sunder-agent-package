extern alias AgentApp;

using AgentApp::Sunder.Package.Agent.PackageViews;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Notifications;
using AgentChatSelectionStateService = AgentApp::Sunder.Package.Agent.Services.AgentChatSelectionStateService;
using AgentToolPresentationService = AgentApp::Sunder.Package.Agent.Services.AgentToolPresentationService;
using static Sunder.Package.Agent.Presentation.Tests.RenderedTranscriptGeometry;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewComposition;

namespace Sunder.Package.Agent.Presentation.Tests;

internal static class RenderedChatFixture
{
    internal static AgentTurnRecord CreateTextTurn(
        Guid sessionId,
        DateTimeOffset createdAtUtc,
        string content)
    {
        var turnId = Guid.NewGuid();
        return new AgentTurnRecord(
            turnId,
            sessionId,
            AgentMessageRole.Assistant,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                turnId,
                0,
                AgentTurnItemKind.Text,
                content,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                false,
                null,
                null)],
            createdAtUtc,
            createdAtUtc);
    }

    // Tool-detail/viewport regressions first reveal compact members, just as a
    // user now must for a singleton. Individual details remain collapsed/lazy.
    internal static async Task RevealCompactCallsAsync(AgentChatView view, IEnumerable<AgentActivityGroupRowViewModel> groups)
    {
        foreach (var group in groups) group.IsExpanded = true;
        await Dispatcher.UIThread.InvokeAsync(view.UpdateLayout, DispatcherPriority.Render);
        await WaitForPendingCoordinatorOperationsAsync(view);
        await Dispatcher.UIThread.InvokeAsync(view.UpdateLayout, DispatcherPriority.Render);
    }

    internal static AgentChatView CreateAgentChatView(
        RegressionTestPackageScope scope,
        AgentServices services,
        AgentProfileService profileService)
        => RegressionTestAppViews.ChatView(
            profileService,
            services.WorkspaceService,
            services.SessionService,
            NoOpPermissionGateway.Instance,
            NoOpRunGateway.Instance,
            new AgentChatSelectionStateService(scope.Context),
            new AgentToolPresentationService(),
            new AgentExecutionTargetWarmupService(
                services.WorkspaceService,
                services.TargetService),
            null!,
            new AgentAttachmentService(scope.Context),
            NullPackageNotificationService.Instance);

    internal static IEnumerable<Border> FindToolDetailCards(Control root)
        => root.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Classes.Contains("tool-output-card"));

    internal class CountingSessionGateway(IAgentSessionGateway inner)
        : IAgentSessionGateway,
            IAgentTranscriptToolDetailGateway
    {
        public int RecentTranscriptReadCount { get; private set; }

        public event Action<Guid>? SessionChanged
        {
            add => inner.SessionChanged += value;
            remove => inner.SessionChanged -= value;
        }

        public event Action<Guid, AgentTurnRecord>? TurnChanged
        {
            add => inner.TurnChanged += value;
            remove => inner.TurnChanged -= value;
        }

        public event Action<Guid>? TranscriptReset
        {
            add => inner.TranscriptReset += value;
            remove => inner.TranscriptReset -= value;
        }

        public event Action<Guid, AgentRunActivityUpdate>? RunActivityChanged
        {
            add => inner.RunActivityChanged += value;
            remove => inner.RunActivityChanged -= value;
        }

        public IReadOnlyList<AgentSessionRecord> ListSessions() => inner.ListSessions();
        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId)
            => inner.ListSessionsForWorkspace(workspaceId);
        public AgentSessionRecord CreateSession(string title, Guid? parentSessionId = null, Guid? rootSessionId = null,
            Guid? parentRunId = null, long? parentRunRevision = null, string? parentToolCallId = null,
            string? taskId = null, string? profileId = null, string? behaviorLoopId = null,
            string? agentKind = null, string? workspaceId = null)
            => inner.CreateSession(title, parentSessionId, rootSessionId, parentRunId, parentRunRevision,
                parentToolCallId, taskId, profileId, behaviorLoopId, agentKind, workspaceId);
        public AgentSessionRecord? GetSession(Guid sessionId) => inner.GetSession(sessionId);
        public void UpdateSession(AgentSessionRecord session) => inner.UpdateSession(session);
        public Task DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
            => inner.DeleteSessionAsync(sessionId, cancellationToken);
        public IReadOnlyList<AgentTurnRecord> ListTurns(Guid sessionId) => inner.ListTurns(sessionId);
        public virtual IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit)
        {
            RecentTranscriptReadCount++;
            return inner.ListRecentTurns(sessionId, limit);
        }
        public virtual IReadOnlyList<AgentTurnRecord> ListTurnsBefore(
            Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit)
            => inner.ListTurnsBefore(sessionId, beforeCreatedAtUtc, beforeTurnId, limit);
        public virtual IReadOnlyList<AgentTurnRecord> ListTurnsAfter(
            Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit)
            => inner.ListTurnsAfter(sessionId, afterCreatedAtUtc, afterTurnId, limit);
        public AgentTurnRecord? GetTurn(Guid turnId) => inner.GetTurn(turnId);
        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => inner.GetLatestCheckpoint(sessionId);
        public virtual Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(
            AgentTranscriptToolDetailRequest request,
            CancellationToken cancellationToken = default)
            => inner is IAgentTranscriptToolDetailGateway detailGateway
                ? detailGateway.LoadToolDetailAsync(request, cancellationToken)
                : Task.FromResult<AgentTranscriptToolDetailRecord?>(null);
    }

    internal sealed class NoOpPermissionGateway : IAgentPermissionGateway
    {
        public static NoOpPermissionGateway Instance { get; } = new();
        public AgentSessionPermissionState GetSessionState(Guid sessionId) => new(sessionId, false);
        public void SetSessionUnrestrictedMode(Guid sessionId, bool isEnabled) { }
        public Task<IReadOnlyList<AgentPermissionActionDescriptor>> ListActionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AgentPermissionActionDescriptor>>([]);
        public IReadOnlyList<AgentPermissionOverride> ListOverrides() => [];
        public void SaveOverride(string actionId, string boundaryId, AgentPermissionDecision decision) { }
        public void DeleteOverride(string actionId, string boundaryId) { }
        public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingRequestsForSessionTree(Guid sessionId) => [];
    }

    internal sealed class NoOpRunGateway : IAgentRunGateway
    {
        public static NoOpRunGateway Instance { get; } = new();
        public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(Guid sessionId, string profileId,
            string userMessage, string workspaceId, IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(Guid sessionId,
            Guid rollbackAnchorTurnId, string profileId, string userMessage, string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentRunCheckpointRecord?> StopAsync(Guid sessionId,
            CancellationToken cancellationToken = default) => Task.FromResult<AgentRunCheckpointRecord?>(null);
        public Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(Guid sessionId, string requestId,
            CancellationToken cancellationToken = default) => Task.FromResult<AgentRunCheckpointRecord?>(null);
        public Task<AgentRunCheckpointRecord?> DenyPendingPermissionAsync(Guid sessionId, string requestId,
            CancellationToken cancellationToken = default) => Task.FromResult<AgentRunCheckpointRecord?>(null);
    }
}
