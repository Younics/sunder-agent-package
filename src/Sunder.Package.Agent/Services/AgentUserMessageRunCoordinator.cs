using System.Diagnostics;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services;

public sealed class AgentUserMessageRunCoordinator(
    AgentSessionService sessionService,
    AgentProfileService profileService,
    AgentWorkspaceService workspaceService,
    AgentMemoryCoordinator memoryCoordinator,
    AgentRunAttachmentStore attachmentStore,
    AgentActiveRunRegistry activeRunRegistry,
    AgentRunEventLogger runEventLogger,
    AgentRunProviderResolver providerResolver,
    AgentBehaviorLoopHostFactory behaviorLoopHostFactory,
    AgentBehaviorLoopResolver behaviorLoopResolver
)
{
    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentProfileService _profileService = profileService;
    private readonly AgentWorkspaceService _workspaceService = workspaceService;
    private readonly AgentMemoryCoordinator _memoryCoordinator = memoryCoordinator;
    private readonly AgentRunAttachmentStore _attachmentStore = attachmentStore;
    private readonly AgentActiveRunRegistry _activeRunRegistry = activeRunRegistry;
    private readonly AgentRunEventLogger _runEventLogger = runEventLogger;
    private readonly AgentRunProviderResolver _providerResolver = providerResolver;
    private readonly AgentBehaviorLoopHostFactory _behaviorLoopHostFactory =
        behaviorLoopHostFactory;
    private readonly AgentBehaviorLoopResolver _behaviorLoopResolver = behaviorLoopResolver;

    public Task<AgentRunCheckpointRecord> QueueAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId
    ) => QueueAsync(sessionId, profileId, userMessage, workspaceId, []);

    public async Task<AgentRunCheckpointRecord> QueueAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments
    )
    {
        var session =
            _sessionService.GetSession(sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' was not found.");
        var nextRevision = _sessionService.GetNextRunRevision(sessionId);
        var runId = Guid.NewGuid();
        var runStartedAtUtc = DateTimeOffset.UtcNow;
        var runStopwatch = Stopwatch.StartNew();

        var workspace = ResolveWorkspace(workspaceId);
        if (workspace is null)
        {
            return _sessionService.SaveCheckpoint(
                sessionId,
                nextRevision,
                AgentRunStatus.Failed,
                "The selected workspace was not found."
            );
        }

        var profile = ResolveProfile(profileId);
        if (profile is null)
        {
            return _sessionService.SaveCheckpoint(
                sessionId,
                nextRevision,
                AgentRunStatus.Failed,
                "The selected agent was not found."
            );
        }

        if (
            !string.Equals(session.ProfileId, profile.ProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                session.BehaviorLoopId,
                profile.BehaviorLoopId,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            session = session with
            {
                ProfileId = profile.ProfileId,
                BehaviorLoopId = profile.BehaviorLoopId,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            _sessionService.UpdateSession(session);
        }

        var providerSelection = _providerResolver.ResolveChatProvider(profile);
        var chatBinding = providerSelection.ChatBinding;
        var provider = providerSelection.Provider;

        _runEventLogger.LogRunEvent(
            PackageLogLevel.Information,
            sessionId,
            runId,
            nextRevision,
            "run.started",
            "Agent run started.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["profile.id"] = profile.ProfileId,
                ["profile.display_name"] = profile.DisplayName,
                ["provider.id"] = chatBinding?.ProviderId,
                ["model.id"] = chatBinding?.ModelId,
                ["workspace.id"] = workspace.WorkspaceId,
            }
        );

        if (
            provider is null
            || chatBinding is null
            || string.IsNullOrWhiteSpace(chatBinding.ModelId)
        )
        {
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Error,
                sessionId,
                runId,
                nextRevision,
                "run.failed",
                "No installed provider matches this profile yet, or no model is selected.",
                runStopwatch.ElapsedMilliseconds
            );
            return _sessionService.SaveCheckpoint(
                sessionId,
                nextRevision,
                AgentRunStatus.Failed,
                "No installed provider matches this profile yet, or no model is selected."
            );
        }

        try
        {
            var readinessStopwatch = Stopwatch.StartNew();
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Information,
                sessionId,
                runId,
                nextRevision,
                "provider.model.selected",
                "Provider model selected.",
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["provider.id"] = provider.Descriptor.ProviderId,
                    ["model.id"] = chatBinding.ModelId,
                }
            );
            var readiness = await provider
                .GetReadinessAsync(CancellationToken.None)
                .ConfigureAwait(false);
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Debug,
                sessionId,
                runId,
                nextRevision,
                "provider.readiness.completed",
                readiness.Message,
                readinessStopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["provider.id"] = provider.Descriptor.ProviderId,
                    ["provider.readiness_status"] = readiness.Status,
                }
            );
            if (readiness.Status != AgentProviderReadinessStatus.Ready)
            {
                return _sessionService.SaveCheckpoint(
                    sessionId,
                    nextRevision,
                    AgentRunStatus.Failed,
                    readiness.Message
                );
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Error,
                sessionId,
                runId,
                nextRevision,
                "provider.readiness.failed",
                "Provider readiness check failed.",
                runStopwatch.ElapsedMilliseconds,
                exception: ex
            );
            return _sessionService.SaveCheckpoint(
                sessionId,
                nextRevision,
                AgentRunStatus.Failed,
                ex.Message
            );
        }

        AgentProviderRunCapabilities runCapabilities;
        AgentModelVariantDescriptor? modelVariant;
        try
        {
            var capabilitiesStopwatch = Stopwatch.StartNew();
            var metadata = await _providerResolver
                .ResolveRunMetadataAsync(provider, chatBinding, CancellationToken.None)
                .ConfigureAwait(false);
            runCapabilities = metadata.RunCapabilities;
            modelVariant = metadata.ModelVariant;
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Debug,
                sessionId,
                runId,
                nextRevision,
                "provider.capabilities.completed",
                runCapabilities.Summary,
                capabilitiesStopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["provider.supports_native_tool_calling"] =
                        runCapabilities.SupportsNativeToolCalling,
                    ["provider.supports_streaming_tool_calls"] =
                        runCapabilities.SupportsStreamingToolCalls,
                    ["provider.supports_multiple_tool_calls"] =
                        runCapabilities.SupportsMultipleToolCalls,
                    ["provider.supports_image_input"] = runCapabilities.SupportsImageInput,
                    ["provider.supports_pdf_input"] = runCapabilities.SupportsPdfInput,
                    ["provider.supports_audio_input"] = runCapabilities.SupportsAudioInput,
                    ["provider.supports_video_input"] = runCapabilities.SupportsVideoInput,
                    ["model.variant_id"] = modelVariant?.VariantId,
                }
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Error,
                sessionId,
                runId,
                nextRevision,
                "provider.capabilities.failed",
                "Provider capability lookup failed.",
                runStopwatch.ElapsedMilliseconds,
                exception: ex
            );
            return _sessionService.SaveCheckpoint(
                sessionId,
                nextRevision,
                AgentRunStatus.Failed,
                ex.Message
            );
        }

        IReadOnlyList<AgentStoredAttachment> storedAttachments;
        try
        {
            storedAttachments = await _attachmentStore
                .StoreAsync(sessionId, attachments, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Error,
                sessionId,
                runId,
                nextRevision,
                "attachment.store.failed",
                ex.Message,
                runStopwatch.ElapsedMilliseconds,
                exception: ex
            );
            return _sessionService.SaveCheckpoint(
                sessionId,
                nextRevision,
                AgentRunStatus.Failed,
                ex.Message
            );
        }

        var interruptedRun = _activeRunRegistry.Remove(sessionId);
        var interruptedCheckpoint = interruptedRun is null
            ? null
            : _sessionService.SaveCheckpoint(
                sessionId,
                interruptedRun.RunRevision,
                AgentRunStatus.Interrupted,
                "Interrupted by a newer user message before provider execution completed."
            );

        if (interruptedRun is not null && interruptedCheckpoint is not null)
        {
            await _memoryCoordinator
                .PublishLifecycleEventAsync(
                    AgentLifecycleEventKind.RunInterrupted,
                    session,
                    profile,
                    interruptedRun.RunId,
                    interruptedRun.RunRevision,
                    AgentRunStatus.Interrupted,
                    interruptedRun.StartedAtUtc,
                    interruptedRun.UserMessage,
                    checkpoint: interruptedCheckpoint,
                    isInterrupted: true,
                    cancellationToken: CancellationToken.None
                )
                .ConfigureAwait(false);
        }

        interruptedRun?.CancellationTokenSource.Cancel();
        interruptedRun?.CancellationTokenSource.Dispose();

        var userTurn =
            storedAttachments.Count == 0
                ? _sessionService.AppendTextTurn(sessionId, AgentMessageRole.User, userMessage)
                : _sessionService.AppendUserTurn(
                    sessionId,
                    AgentMessageRole.User,
                    userMessage,
                    storedAttachments
                );
        _runEventLogger.LogRunEvent(
            PackageLogLevel.Debug,
            sessionId,
            runId,
            nextRevision,
            "turn.user.appended",
            "User turn appended.",
            runStopwatch.ElapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["turn.content_length"] = userMessage.Length,
                ["turn.attachment_count"] = storedAttachments.Count,
                ["turn.attachment_bytes"] = storedAttachments.Sum(attachment =>
                    attachment.Metadata.SizeBytes
                ),
            }
        );
        await _memoryCoordinator
            .PublishLifecycleEventAsync(
                AgentLifecycleEventKind.UserTurnAdded,
                session,
                profile,
                runId,
                nextRevision,
                AgentRunStatus.Running,
                runStartedAtUtc,
                userMessage,
                triggerTurn: userTurn,
                cancellationToken: CancellationToken.None
            )
            .ConfigureAwait(false);
        var profileHasCapabilityAssignments =
            (profile.SelectableCapabilityAssignments?.Count ?? 0) > 0;
        var runningSummary =
            runCapabilities.SupportsNativeToolCalling
                ? "User message queued. Provider execution is starting."
            : profileHasCapabilityAssignments
                ? $"User message queued. Provider execution is starting in text-only mode. {runCapabilities.Summary}"
            : "User message queued. Provider execution is starting.";
        var runningCheckpoint = _sessionService.SaveCheckpoint(
            sessionId,
            nextRevision,
            AgentRunStatus.Running,
            runningSummary
        );
        _runEventLogger.LogRunEvent(
            PackageLogLevel.Debug,
            sessionId,
            runId,
            nextRevision,
            "run.running_checkpoint.saved",
            runningSummary,
            runStopwatch.ElapsedMilliseconds
        );

        var runHandle = new AgentActiveRunHandle(
            runId,
            nextRevision,
            runStartedAtUtc,
            profile.ProfileId,
            userMessage,
            new CancellationTokenSource()
        );
        _activeRunRegistry.Set(sessionId, runHandle);

        try
        {
            var executionBinding = ResolveExecutionBinding(workspace);
            var host = _behaviorLoopHostFactory.Create(
                provider,
                session,
                profile,
                workspace,
                runId,
                nextRevision,
                runStartedAtUtc,
                userMessage,
                userTurn.TurnId
            );
            var behaviorLoop = _behaviorLoopResolver.Resolve(profile);
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Debug,
                sessionId,
                runId,
                nextRevision,
                "behavior.loop.selected",
                "Behavior loop selected.",
                runStopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["behavior.loop_id"] = behaviorLoop.Descriptor.LoopId,
                }
            );
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
                        runId,
                        nextRevision,
                        runningCheckpoint,
                        runStartedAtUtc,
                        userMessage,
                        userTurn.TurnId,
                        modelVariant
                    ),
                    host,
                    runHandle.CancellationTokenSource.Token
                )
                .ConfigureAwait(false);
            loopResult = ResolveStoppedOrInterruptedRunResult(sessionId, nextRevision, loopResult);
            _runEventLogger.LogRunCompletion(
                sessionId,
                runId,
                nextRevision,
                loopResult,
                runStopwatch.ElapsedMilliseconds
            );
            return loopResult.Checkpoint;
        }
        catch (OperationCanceledException)
        {
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Warning,
                sessionId,
                runId,
                nextRevision,
                "run.canceled",
                "Agent run was canceled.",
                runStopwatch.ElapsedMilliseconds
            );
            return GetStoppedOrInterruptedCheckpoint(sessionId, nextRevision)
                ?? interruptedCheckpoint
                ?? runningCheckpoint;
        }
        catch (Exception ex)
        {
            if (!_activeRunRegistry.IsCurrent(sessionId, nextRevision))
            {
                return interruptedCheckpoint ?? runningCheckpoint;
            }

            var assistantTurn = _sessionService.AppendTextTurn(
                sessionId,
                AgentMessageRole.Assistant,
                $"### Agent run failed\n\n{ex.Message}"
            );

            var failedCheckpoint = _sessionService.SaveCheckpoint(
                sessionId,
                nextRevision,
                AgentRunStatus.Failed,
                ex.Message
            );
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Error,
                sessionId,
                runId,
                nextRevision,
                "run.failed",
                ex.Message,
                runStopwatch.ElapsedMilliseconds,
                exception: ex
            );
            await _memoryCoordinator
                .PublishLifecycleEventAsync(
                    AgentLifecycleEventKind.RunFailed,
                    session,
                    profile,
                    runId,
                    nextRevision,
                    AgentRunStatus.Failed,
                    runStartedAtUtc,
                    userMessage,
                    triggerTurn: assistantTurn,
                    checkpoint: failedCheckpoint,
                    cancellationToken: CancellationToken.None
                )
                .ConfigureAwait(false);
            return failedCheckpoint;
        }
        finally
        {
            _activeRunRegistry.CleanupCurrent(sessionId, nextRevision);
        }
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

    private AgentBehaviorLoopResult ResolveStoppedOrInterruptedRunResult(
        Guid sessionId,
        long runRevision,
        AgentBehaviorLoopResult loopResult
    )
    {
        if (
            loopResult.Checkpoint.Status != AgentRunStatus.Running
            || _activeRunRegistry.IsCurrent(sessionId, runRevision)
        )
        {
            return loopResult;
        }

        var replacement = GetStoppedOrInterruptedCheckpoint(sessionId, runRevision);
        return replacement is null
            ? loopResult
            : new AgentBehaviorLoopResult(replacement, ToCompletionKind(replacement.Status));
    }

    private AgentRunCheckpointRecord? GetStoppedOrInterruptedCheckpoint(
        Guid sessionId,
        long runRevision
    )
    {
        var latest = _sessionService.GetLatestCheckpoint(sessionId);
        return
            latest is not null
            && latest.RunRevision == runRevision
            && latest.Status is AgentRunStatus.Stopped or AgentRunStatus.Interrupted
            ? latest
            : null;
    }

    private static AgentBehaviorLoopCompletionKind ToCompletionKind(AgentRunStatus status) =>
        status switch
        {
            AgentRunStatus.Completed => AgentBehaviorLoopCompletionKind.Completed,
            AgentRunStatus.WaitingForApproval => AgentBehaviorLoopCompletionKind.WaitingForApproval,
            AgentRunStatus.Stopped => AgentBehaviorLoopCompletionKind.Stopped,
            AgentRunStatus.Interrupted => AgentBehaviorLoopCompletionKind.Interrupted,
            _ => AgentBehaviorLoopCompletionKind.Failed,
        };
}
