using System.Diagnostics;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services;

public sealed class AgentPermissionResumeCoordinator(
    AgentSessionService sessionService,
    AgentWorkspaceService workspaceService,
    AgentProfileService profileService,
    AgentPermissionService permissionService,
    AgentRunProviderResolver providerResolver,
    AgentActiveRunRegistry activeRunRegistry,
    AgentRunEventLogger runEventLogger,
    AgentBehaviorLoopHostFactory behaviorLoopHostFactory,
    AgentBehaviorLoopResolver behaviorLoopResolver,
    AgentParentRunContinuationService parentRunContinuationService
)
{
    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentWorkspaceService _workspaceService = workspaceService;
    private readonly AgentProfileService _profileService = profileService;
    private readonly AgentPermissionService _permissionService = permissionService;
    private readonly AgentRunProviderResolver _providerResolver = providerResolver;
    private readonly AgentActiveRunRegistry _activeRunRegistry = activeRunRegistry;
    private readonly AgentRunEventLogger _runEventLogger = runEventLogger;
    private readonly AgentBehaviorLoopHostFactory _behaviorLoopHostFactory =
        behaviorLoopHostFactory;
    private readonly AgentBehaviorLoopResolver _behaviorLoopResolver = behaviorLoopResolver;
    private readonly AgentParentRunContinuationService _parentRunContinuationService =
        parentRunContinuationService;

    public async Task<AgentRunCheckpointRecord?> ApproveAsync(Guid sessionId, string requestId)
    {
        var pending = _permissionService.GetPendingRequest(sessionId, requestId);
        if (pending is null)
        {
            return _sessionService.GetLatestCheckpoint(sessionId);
        }

        var session = _sessionService.GetSession(sessionId);
        if (session is null)
        {
            return _sessionService.GetLatestCheckpoint(sessionId);
        }

        var workspace = ResolveWorkspace(pending.WorkspaceId);
        if (workspace is null)
        {
            return _sessionService.SaveCheckpoint(
                sessionId,
                pending.RunRevision,
                AgentRunStatus.Failed,
                "The workspace used for this run was not found."
            );
        }

        var profile = ResolveProfile(pending.ProfileId);
        if (profile is null)
        {
            return _sessionService.SaveCheckpoint(
                sessionId,
                pending.RunRevision,
                AgentRunStatus.Failed,
                "The agent used for this run was not found."
            );
        }

        var providerSelection = _providerResolver.ResolveChatProvider(profile);
        var chatBinding = providerSelection.ChatBinding;
        var provider = providerSelection.Provider;
        if (
            provider is null
            || chatBinding is null
            || string.IsNullOrWhiteSpace(chatBinding.ModelId)
        )
        {
            return _sessionService.SaveCheckpoint(
                sessionId,
                pending.RunRevision,
                AgentRunStatus.Failed,
                "No installed provider matches this profile yet, or no model is selected."
            );
        }

        if (_activeRunRegistry.IsActive(sessionId))
        {
            return _sessionService.GetLatestCheckpoint(sessionId);
        }

        _permissionService.DeletePendingRequest(sessionId, requestId);
        var runHandle = new AgentActiveRunHandle(
            pending.RunId,
            pending.RunRevision,
            pending.CreatedAtUtc,
            profile.ProfileId,
            pending.UserMessage,
            new CancellationTokenSource()
        );
        var resumeStopwatch = Stopwatch.StartNew();
        _runEventLogger.LogRunEvent(
            PackageLogLevel.Information,
            sessionId,
            pending.RunId,
            pending.RunRevision,
            "permission.approved_resume.start",
            "Resuming run after permission approval.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tool.id"] = pending.ToolId,
                ["permission.action_id"] = pending.ActionId,
                ["permission.boundary_id"] = pending.BoundaryId,
                ["permission.request_id"] = pending.RequestId,
            }
        );
        _activeRunRegistry.Set(sessionId, runHandle);

        try
        {
            var host = _behaviorLoopHostFactory.Create(
                provider,
                session,
                profile,
                workspace,
                pending.RunId,
                pending.RunRevision,
                pending.CreatedAtUtc,
                pending.UserMessage,
                pending.UserTurnId
            );
            var approvedToolOutcome = await host.HandleApprovedToolCallAsync(
                    pending,
                    runHandle.CancellationTokenSource.Token
                )
                .ConfigureAwait(false);
            if (approvedToolOutcome.Kind != AgentToolCallOutcomeKind.Executed)
            {
                _runEventLogger.LogRunEvent(
                    PackageLogLevel.Warning,
                    sessionId,
                    pending.RunId,
                    pending.RunRevision,
                    "permission.approved_resume.failed",
                    approvedToolOutcome.Kind.ToString(),
                    resumeStopwatch.ElapsedMilliseconds
                );
                return approvedToolOutcome.Checkpoint
                    ?? _sessionService.GetLatestCheckpoint(sessionId);
            }

            AgentProviderRunCapabilities runCapabilities;
            AgentModelVariantDescriptor? modelVariant;
            try
            {
                var metadata = await _providerResolver
                    .ResolveRunMetadataAsync(
                        provider,
                        chatBinding,
                        runHandle.CancellationTokenSource.Token
                    )
                    .ConfigureAwait(false);
                runCapabilities = metadata.RunCapabilities;
                modelVariant = metadata.ModelVariant;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return _sessionService.SaveCheckpoint(
                    sessionId,
                    pending.RunRevision,
                    AgentRunStatus.Failed,
                    ex.Message
                );
            }

            if (!runCapabilities.SupportsNativeToolCalling)
            {
                return _sessionService.SaveCheckpoint(
                    sessionId,
                    pending.RunRevision,
                    AgentRunStatus.Completed,
                    "Approved tool call executed."
                );
            }

            var runningCheckpoint = _sessionService.SaveCheckpoint(
                session.SessionId,
                pending.RunRevision,
                AgentRunStatus.Running,
                $"Approved tool '{pending.ToolId}' completed. Continuing provider execution."
            );
            var executionBinding = ResolveExecutionBinding(workspace);
            var behaviorLoop = _behaviorLoopResolver.Resolve(profile);
            var loopResult = await behaviorLoop
                .RunAsync(
                    new AgentBehaviorLoopContext(
                        session,
                        profile,
                        provider.Descriptor.ProviderId,
                        chatBinding.ModelId,
                        runCapabilities,
                        workspace,
                        executionBinding,
                        pending.RunId,
                        pending.RunRevision,
                        runningCheckpoint,
                        pending.CreatedAtUtc,
                        pending.UserMessage,
                        pending.UserTurnId,
                        modelVariant
                    ),
                    host,
                    runHandle.CancellationTokenSource.Token
                )
                .ConfigureAwait(false);
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Information,
                sessionId,
                pending.RunId,
                pending.RunRevision,
                "permission.approved_resume.completed",
                loopResult.CompletionKind.ToString(),
                resumeStopwatch.ElapsedMilliseconds
            );
            var parentCheckpoint = await _parentRunContinuationService
                .TryResumeAfterChildCompletionAsync(
                    session,
                    loopResult.Checkpoint,
                    workspace.WorkspaceId,
                    runHandle.CancellationTokenSource.Token
                )
                .ConfigureAwait(false);
            return parentCheckpoint ?? loopResult.Checkpoint;
        }
        catch (OperationCanceledException)
        {
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Warning,
                sessionId,
                pending.RunId,
                pending.RunRevision,
                "permission.approved_resume.canceled",
                "Approved permission resume was canceled.",
                resumeStopwatch.ElapsedMilliseconds
            );
            return _sessionService.GetLatestCheckpoint(sessionId);
        }
        finally
        {
            _activeRunRegistry.CleanupCurrent(sessionId, pending.RunRevision);
        }
    }

    public AgentRunCheckpointRecord? Deny(Guid sessionId, string requestId)
    {
        var pending = _permissionService.GetPendingRequest(sessionId, requestId);
        if (pending is null)
        {
            return _sessionService.GetLatestCheckpoint(sessionId);
        }

        var session = _sessionService.GetSession(sessionId);
        _permissionService.DeletePendingRequest(sessionId, requestId);
        _sessionService.AppendToolResultTurn(
            sessionId,
            pending.CallId,
            pending.ToolId ?? string.Empty,
            pending.ArgumentsJson,
            $"Permission denied: tool '{pending.ToolId}' was not executed.",
            "Permission request denied.",
            structuredPayloadJson: null,
            sourcesJson: null,
            wasTruncated: false,
            isError: true,
            errorCode: "permission-denied",
            backendId: null
        );
        var stoppedCheckpoint = _sessionService.SaveCheckpoint(
            sessionId,
            pending.RunRevision,
            AgentRunStatus.Stopped,
            "Permission request denied."
        );
        if (session is not null)
        {
            _parentRunContinuationService.TryRecordParentTaskFailureAfterChildStop(
                session,
                stoppedCheckpoint
            );
        }

        return stoppedCheckpoint;
    }

    private AgentWorkspaceRecord? ResolveWorkspace(string? workspaceId) =>
        string.IsNullOrWhiteSpace(workspaceId)
            ? null
            : _workspaceService.GetWorkspace(workspaceId.Trim());

    private AgentProfileRecord? ResolveProfile(string? profileId) =>
        string.IsNullOrWhiteSpace(profileId) ? null : _profileService.GetProfile(profileId);

    private AgentWorkspaceBindingRecord? ResolveExecutionBinding(AgentWorkspaceRecord? workspace) =>
        workspace is null
            ? null
            : _workspaceService
                .ListBindings(workspace.WorkspaceId)
                .FirstOrDefault(binding =>
                    binding.IsEnabled
                    && string.Equals(
                        binding.Role,
                        AgentWorkspaceBindingRoles.PrimaryExecutionTarget,
                        StringComparison.OrdinalIgnoreCase
                    )
                );
}
