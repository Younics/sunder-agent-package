using System.Diagnostics;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services;

internal sealed class AgentRunPreparationService(
    AgentSessionService sessionService,
    AgentProfileService profileService,
    AgentWorkspaceService workspaceService,
    AgentRunAttachmentStore attachmentStore,
    AgentRunEventLogger runEventLogger,
    AgentRunProviderResolver providerResolver,
    AgentSessionTitleService? sessionTitleService = null)
{
    private const string MissingProviderSummary =
        "No installed provider matches this profile yet, or no model is selected.";

    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentProfileService _profileService = profileService;
    private readonly AgentWorkspaceService _workspaceService = workspaceService;
    private readonly AgentRunAttachmentStore _attachmentStore = attachmentStore;
    private readonly AgentRunEventLogger _runEventLogger = runEventLogger;
    private readonly AgentRunProviderResolver _providerResolver = providerResolver;
    private readonly AgentSessionTitleService? _sessionTitleService = sessionTitleService;

    internal AgentRunAttachmentStore AttachmentStore => _attachmentStore;

    internal async Task<AgentRunPreparationResult> PrepareAsync(
        AgentSessionRecord session,
        AgentDurableRunRecord reservedRun,
        AgentActiveRunHandle runHandle,
        string profileId,
        string workspaceId,
        IReadOnlyList<AgentStoredAttachment> attachments,
        Guid? rollbackAnchorTurnId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var shouldGenerateSessionTitle = reservedRun.UserTurnId is { } userTurnId
            ? _sessionTitleService?.ShouldGenerateTitleForAdmittedUserMessage(session, userTurnId) == true
            : _sessionTitleService?.ShouldGenerateTitleForFirstUserMessage(session) == true;
        var workspace = ResolveWorkspace(workspaceId);
        if (workspace is null)
        {
            return Failed("The selected workspace was not found.");
        }

        if (string.IsNullOrWhiteSpace(session.WorkspaceId))
        {
            return Failed("The selected session is not assigned to a workspace.");
        }

        if (!string.Equals(
                session.WorkspaceId,
                workspace.WorkspaceId,
                StringComparison.OrdinalIgnoreCase))
        {
            return Failed("The selected session belongs to a different workspace.");
        }

        var profile = ResolveProfile(profileId);
        if (profile is null)
        {
            return Failed("The selected agent was not found.");
        }

        session = SynchronizeSessionProfile(session, profile);
        var providerSelection = _providerResolver.ResolveChatProvider(profile);
        var selectionTransferred = false;
        try
        {
            LogRunStarted(reservedRun, profile, workspace, providerSelection.ChatBinding);
            if (!providerSelection.IsAvailable
                || providerSelection.ChatBinding is null
                || string.IsNullOrWhiteSpace(providerSelection.ChatBinding.ModelId))
            {
                _runEventLogger.LogRunEvent(
                    PackageLogLevel.Error,
                    session.SessionId,
                    reservedRun.Key.RunId,
                    reservedRun.Key.RunRevision,
                    "run.failed",
                    MissingProviderSummary,
                    ElapsedMilliseconds(reservedRun));
                return Failed(MissingProviderSummary);
            }

            var chatBinding = providerSelection.ChatBinding;
            var readinessFailure = await CheckReadinessAsync(
                session.SessionId,
                reservedRun,
                providerSelection,
                chatBinding,
                cancellationToken).ConfigureAwait(false);
            if (readinessFailure is not null)
            {
                return Failed(readinessFailure);
            }

            var metadataResult = await ResolveMetadataAsync(
                session.SessionId,
                reservedRun,
                providerSelection,
                cancellationToken).ConfigureAwait(false);
            if (metadataResult.FailureSummary is not null)
            {
                return Failed(metadataResult.FailureSummary);
            }

            var metadata = metadataResult.Metadata!;
            selectionTransferred = true;
            return new AgentRunPrepared(new AgentRunPlan(
                reservedRun.Key,
                runHandle,
                reservedRun.StartedAtUtc,
                session,
                profile,
                workspace,
                providerSelection,
                chatBinding,
                metadata.RunCapabilities,
                metadata.ModelVariant,
                metadata.ModelSpeedOption,
                metadata.ModelModeOption,
                attachments.ToArray(),
                reservedRun.UserMessage,
                rollbackAnchorTurnId,
                shouldGenerateSessionTitle));
        }
        finally
        {
            if (!selectionTransferred)
            {
                providerSelection.Dispose();
            }
        }
    }

    private async Task<string?> CheckReadinessAsync(
        Guid sessionId,
        AgentDurableRunRecord run,
        AgentRunProviderSelection selection,
        AgentProfileModelBindingRecord chatBinding,
        CancellationToken cancellationToken)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Information,
                sessionId,
                run.Key.RunId,
                run.Key.RunRevision,
                "provider.model.selected",
                "Provider model selected.",
                attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["provider.id"] = selection.Descriptor!.ProviderId,
                    ["model.id"] = chatBinding.ModelId,
                });
            var readiness = await selection.InvokeAsync(
                cancellationToken,
                static (provider, token) => provider.GetReadinessAsync(token)).ConfigureAwait(false);
            _runEventLogger.LogRunEvent(
                PackageLogLevel.Debug,
                sessionId,
                run.Key.RunId,
                run.Key.RunRevision,
                "provider.readiness.completed",
                readiness.Message,
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["provider.id"] = selection.Descriptor.ProviderId,
                    ["provider.readiness_status"] = readiness.Status,
                });
            return readiness.Status == AgentProviderReadinessStatus.Ready
                ? null
                : readiness.Message;
        }
        catch (AgentPackageUnavailableException ex)
        {
            return ex.Message;
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
                run.Key.RunId,
                run.Key.RunRevision,
                "provider.readiness.failed",
                "Provider readiness check failed.",
                ElapsedMilliseconds(run),
                exception: ex);
            return ex.Message;
        }
    }

    private async Task<MetadataPreparationResult> ResolveMetadataAsync(
        Guid sessionId,
        AgentDurableRunRecord run,
        AgentRunProviderSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var metadata = await _providerResolver
                .ResolveRunMetadataAsync(selection, cancellationToken)
                .ConfigureAwait(false);
            LogCapabilities(sessionId, run, metadata, stopwatch.ElapsedMilliseconds);
            return new MetadataPreparationResult(metadata, null);
        }
        catch (AgentPackageUnavailableException ex)
        {
            return new MetadataPreparationResult(null, ex.Message);
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
                run.Key.RunId,
                run.Key.RunRevision,
                "provider.capabilities.failed",
                "Provider capability lookup failed.",
                ElapsedMilliseconds(run),
                exception: ex);
            return new MetadataPreparationResult(null, ex.Message);
        }
    }

    private AgentSessionRecord SynchronizeSessionProfile(
        AgentSessionRecord session,
        AgentProfileRecord profile)
    {
        if (string.Equals(session.ProfileId, profile.ProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                session.BehaviorLoopId,
                profile.BehaviorLoopId,
                StringComparison.OrdinalIgnoreCase))
        {
            return session;
        }

        var updated = session with
        {
            ProfileId = profile.ProfileId,
            BehaviorLoopId = profile.BehaviorLoopId,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        _sessionService.UpdateSession(updated);
        return updated;
    }

    private void LogRunStarted(
        AgentDurableRunRecord run,
        AgentProfileRecord profile,
        AgentWorkspaceRecord workspace,
        AgentProfileModelBindingRecord? chatBinding) =>
        _runEventLogger.LogRunEvent(
            PackageLogLevel.Information,
            run.Key.SessionId,
            run.Key.RunId,
            run.Key.RunRevision,
            "run.started",
            "Agent run started.",
            attributes: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["profile.id"] = profile.ProfileId,
                ["profile.display_name"] = profile.DisplayName,
                ["provider.id"] = chatBinding?.ProviderId,
                ["model.id"] = chatBinding?.ModelId,
                ["workspace.id"] = workspace.WorkspaceId,
            });

    private void LogCapabilities(
        Guid sessionId,
        AgentDurableRunRecord run,
        AgentRunProviderMetadata metadata,
        long elapsedMilliseconds) =>
        _runEventLogger.LogRunEvent(
            PackageLogLevel.Debug,
            sessionId,
            run.Key.RunId,
            run.Key.RunRevision,
            "provider.capabilities.completed",
            metadata.RunCapabilities.Summary,
            elapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["provider.supports_native_tool_calling"] =
                    metadata.RunCapabilities.SupportsNativeToolCalling,
                ["provider.supports_streaming_tool_calls"] =
                    metadata.RunCapabilities.SupportsStreamingToolCalls,
                ["provider.supports_multiple_tool_calls"] =
                    metadata.RunCapabilities.SupportsMultipleToolCalls,
                ["provider.supports_image_input"] =
                    metadata.RunCapabilities.SupportsImageInput,
                ["provider.supports_pdf_input"] = metadata.RunCapabilities.SupportsPdfInput,
                ["provider.supports_audio_input"] =
                    metadata.RunCapabilities.SupportsAudioInput,
                ["provider.supports_video_input"] =
                    metadata.RunCapabilities.SupportsVideoInput,
                ["model.variant_id"] = metadata.ModelVariant?.VariantId,
                ["model.speed_option_id"] = metadata.ModelSpeedOption?.SpeedOptionId,
                ["model.mode_option_id"] = metadata.ModelModeOption?.ModeOptionId,
            });

    private AgentWorkspaceRecord? ResolveWorkspace(string? workspaceId) =>
        string.IsNullOrWhiteSpace(workspaceId)
            ? null
            : _workspaceService.GetWorkspace(workspaceId.Trim());

    private AgentProfileRecord? ResolveProfile(string? profileId) =>
        string.IsNullOrWhiteSpace(profileId) ? null : _profileService.GetProfile(profileId);

    private static AgentRunPreparationFailed Failed(string summary) => new(summary);

    private static long ElapsedMilliseconds(AgentDurableRunRecord run) =>
        Math.Max(0, (long)(DateTimeOffset.UtcNow - run.StartedAtUtc).TotalMilliseconds);

    private sealed record MetadataPreparationResult(
        AgentRunProviderMetadata? Metadata,
        string? FailureSummary);

}
