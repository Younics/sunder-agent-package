using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.Services;

internal sealed class AgentUserTurnAdmissionService(
    AgentSessionService sessionService,
    AgentRunAttachmentStore attachmentStore,
    AgentActiveRunRegistry activeRunRegistry,
    AgentSessionTransitionGate? transitionGate = null,
    AgentSessionDeletionFence? deletionFence = null)
{
    private readonly AgentSessionService _sessionService = sessionService;
    private readonly AgentRunAttachmentStore _attachmentStore = attachmentStore;
    private readonly AgentActiveRunRegistry _activeRunRegistry = activeRunRegistry;
    private readonly AgentSessionTransitionGate _transitionGate =
        transitionGate ?? AgentSessionTransitionGate.Shared;
    private readonly AgentSessionDeletionFence _deletionFence =
        deletionFence ?? AgentSessionDeletionFence.Shared;

    internal async Task<AgentUserTurnAdmissionResult> AdmitAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadRequest> attachments,
        Guid userTurnId,
        Guid? rollbackAnchorTurnId,
        CancellationToken cancellationToken)
    {
        var descriptors = attachments
            .Select(attachment => new AgentAdmissionAttachmentDescriptor(
                attachment.FileName,
                attachment.MediaType,
                attachment.Content.LongLength,
                Convert.ToHexString(SHA256.HashData(attachment.Content)).ToLowerInvariant()))
            .ToArray();
        IReadOnlyList<AgentStoredAttachment> stored = [];
        try
        {
            ValidateSessionBeforeAdoption(sessionId);
            stored = await _attachmentStore
                .StoreAsync(sessionId, userTurnId, attachments, cancellationToken)
                .ConfigureAwait(false);
            var result = await AdmitStoredAsync(
                sessionId,
                profileId,
                userMessage,
                workspaceId,
                stored,
                descriptors,
                userTurnId,
                rollbackAnchorTurnId,
                cancellationToken).ConfigureAwait(false);
            if (result.IsExisting)
            {
                _attachmentStore.Cleanup(stored);
            }
            return result;
        }
        catch (Exception exception)
        {
            var durability = ResolveFailureDurability(sessionId, userTurnId, stored, exception);
            if (durability == AdmissionFailureDurability.NotCommitted)
            {
                _attachmentStore.Cleanup(stored);
            }
            else if (durability == AdmissionFailureDurability.Committed)
            {
                TryCancelSupersededProcessRun(sessionId, userTurnId);
            }
            throw;
        }
    }

    internal async Task<AgentUserTurnAdmissionResult> AdmitTransferredAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentAttachmentUploadHandle> handles,
        AgentAttachmentTransferService transferService,
        Guid userTurnId,
        Guid? rollbackAnchorTurnId,
        CancellationToken cancellationToken)
    {
        ValidateSessionBeforeAdoption(sessionId);
        var adoption = transferService.BeginAdoption(handles);
        IReadOnlyList<AgentStoredAttachment> stored = [];
        AgentUserTurnAdmissionResult? result = null;
        var admissionAttempted = false;
        try
        {
            stored = await _attachmentStore
                .AdoptAsync(sessionId, userTurnId, adoption.Uploads, cancellationToken)
                .ConfigureAwait(false);
            var descriptors = adoption.Uploads
                .Select(upload => new AgentAdmissionAttachmentDescriptor(
                    upload.Descriptor.FileName,
                    upload.Descriptor.MediaType,
                    upload.Descriptor.SizeBytes,
                    upload.Descriptor.Sha256))
                .ToArray();
            admissionAttempted = true;
            result = await AdmitStoredAsync(
                sessionId,
                profileId,
                userMessage,
                workspaceId,
                stored,
                descriptors,
                userTurnId,
                rollbackAnchorTurnId,
                cancellationToken).ConfigureAwait(false);
            transferService.CompleteAdoption(adoption);
            if (result.IsExisting)
            {
                _attachmentStore.Cleanup(stored);
            }
            return result;
        }
        catch (Exception exception)
        {
            var durability = !admissionAttempted
                ? AdmissionFailureDurability.NotCommitted
                : result is null
                ? ResolveFailureDurability(sessionId, userTurnId, stored, exception)
                : result.IsExisting
                    ? AdmissionFailureDurability.NotCommitted
                    : AdmissionFailureDurability.Committed;
            if (durability == AdmissionFailureDurability.NotCommitted)
            {
                _attachmentStore.Cleanup(stored);
                transferService.ReleaseAdoption(adoption);
            }
            else if (durability == AdmissionFailureDurability.Committed)
            {
                TryCancelSupersededProcessRun(sessionId, userTurnId);
                TryCompleteAdoption(transferService, adoption);
            }
            else
            {
                transferService.ReleaseAdoption(adoption);
            }
            throw;
        }
    }

    internal AgentRunCommandStatus GetCommandStatus(Guid sessionId, Guid userTurnId)
    {
        var run = _sessionService.Store.GetRunByUserTurnId(userTurnId);
        return run?.Key.SessionId == sessionId
            ? AgentRunCommandStatus.Committed
            : AgentRunCommandStatus.Absent;
    }

    private async Task<AgentUserTurnAdmissionResult> AdmitStoredAsync(
        Guid sessionId,
        string profileId,
        string userMessage,
        string workspaceId,
        IReadOnlyList<AgentStoredAttachment> storedAttachments,
        IReadOnlyList<AgentAdmissionAttachmentDescriptor> fingerprintAttachments,
        Guid userTurnId,
        Guid? rollbackAnchorTurnId,
        CancellationToken cancellationToken)
    {
        if (userTurnId == Guid.Empty)
        {
            throw new AgentUserTurnAdmissionException("User turn id cannot be empty.");
        }

        profileId = Require(profileId, "Profile id");
        workspaceId = Require(workspaceId, "Workspace id");
        userMessage ??= string.Empty;
        var admissionKind = rollbackAnchorTurnId is null
            ? AgentRunAdmissionKind.Normal
            : AgentRunAdmissionKind.Rollback;
        var fingerprint = AgentUserTurnRequestFingerprint.Compute(
            sessionId,
            profileId,
            workspaceId,
            userMessage,
            admissionKind,
            rollbackAnchorTurnId,
            fingerprintAttachments);
        var request = new AgentUserTurnAdmissionRequest(
            userTurnId,
            sessionId,
            profileId,
            workspaceId,
            userMessage,
            admissionKind,
            rollbackAnchorTurnId,
            fingerprint,
            storedAttachments);

        var cleaners = admissionKind == AgentRunAdmissionKind.Rollback
            ? await _sessionService.SnapshotSessionDataCleanersAsync(cancellationToken).ConfigureAwait(false)
            : [];
        AgentUserTurnAdmissionResult result;
        using (await _transitionGate.EnterAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var session = _sessionService.GetSession(sessionId)
                ?? throw new AgentUserTurnAdmissionException($"Session '{sessionId}' was deleted before admission.");
            if (_deletionFence.IsFenced(session))
            {
                throw new AgentUserTurnAdmissionException("The session is being deleted and cannot admit a new run.");
            }
            if (_sessionService.Store.GetRunByUserTurnId(userTurnId) is null
                && !string.Equals(session.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
            {
                throw new AgentUserTurnAdmissionException(
                    "The selected session belongs to a different workspace.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            result = _sessionService.AdmitUserTurn(request, cleaners);
            if (!result.IsExisting)
            {
                CancelSupersededProcessRun(sessionId, result.Run.Key);
            }
        }

        await _sessionService.CompleteAdmissionCleanupAsync(result).ConfigureAwait(false);
        return result;
    }

    private void ValidateSessionBeforeAdoption(Guid sessionId)
    {
        var session = _sessionService.GetSession(sessionId)
            ?? throw new AgentUserTurnAdmissionException($"Session '{sessionId}' was not found.");
        if (_deletionFence.IsFenced(session))
        {
            throw new AgentUserTurnAdmissionException("The session is being deleted and cannot admit a new run.");
        }
    }

    private void CancelSupersededProcessRun(Guid sessionId, AgentDurableRunKey admittedRun)
    {
        var active = _activeRunRegistry.RemoveIfOlderThan(sessionId, admittedRun.RunRevision);
        if (active is null)
        {
            return;
        }

        try
        {
            active.CancellationTokenSource.Cancel();
        }
        catch
        {
            // Durable supersession is authoritative even if a process-local cancellation callback fails.
        }
    }

    private void TryCancelSupersededProcessRun(Guid sessionId, Guid userTurnId)
    {
        try
        {
            var committedRun = _sessionService.Store.GetRunByUserTurnId(userTurnId);
            if (committedRun?.Key.SessionId == sessionId)
            {
                CancelSupersededProcessRun(sessionId, committedRun.Key);
            }
        }
        catch
        {
            // Preserve the post-commit failure; startup recovery remains authoritative.
        }
    }

    private AdmissionFailureDurability ResolveFailureDurability(
        Guid sessionId,
        Guid userTurnId,
        IReadOnlyList<AgentStoredAttachment> storedAttachments,
        Exception exception)
    {
        if (exception is AgentUserTurnConflictException)
        {
            return AdmissionFailureDurability.NotCommitted;
        }

        try
        {
            var run = _sessionService.Store.GetRunByUserTurnId(userTurnId);
            if (run?.Key.SessionId != sessionId)
            {
                return AdmissionFailureDurability.NotCommitted;
            }
            if (storedAttachments.Count == 0)
            {
                return AdmissionFailureDurability.Committed;
            }

            var turn = _sessionService.Store.GetTurn(userTurnId);
            if (turn is null)
            {
                return AdmissionFailureDurability.NotCommitted;
            }
            return TryReferencesExactAttachments(turn, storedAttachments, out var referencesExactAttachments)
                ? referencesExactAttachments
                    ? AdmissionFailureDurability.Committed
                    : AdmissionFailureDurability.NotCommitted
                : AdmissionFailureDurability.Unknown;
        }
        catch
        {
            return AdmissionFailureDurability.Unknown;
        }
    }

    private static bool TryReferencesExactAttachments(
        AgentTurnRecord turn,
        IReadOnlyList<AgentStoredAttachment> storedAttachments,
        out bool referencesExactAttachments)
    {
        var durableAttachments = new List<AgentAttachmentMetadata>(storedAttachments.Count);
        foreach (var item in turn.Items.Where(item => item.Kind == AgentTurnItemKind.Attachment))
        {
            if (string.IsNullOrWhiteSpace(item.StructuredPayloadJson))
            {
                referencesExactAttachments = false;
                return false;
            }

            try
            {
                var metadata = JsonSerializer.Deserialize<AgentAttachmentMetadata>(item.StructuredPayloadJson);
                if (metadata is null)
                {
                    referencesExactAttachments = false;
                    return false;
                }
                durableAttachments.Add(metadata);
            }
            catch (JsonException)
            {
                referencesExactAttachments = false;
                return false;
            }
        }

        referencesExactAttachments = durableAttachments.Count == storedAttachments.Count
            && durableAttachments.Zip(storedAttachments).All(pair =>
                pair.First.AttachmentId == pair.Second.Metadata.AttachmentId
                && string.Equals(
                    pair.First.StorageRelativePath,
                    pair.Second.Metadata.StorageRelativePath,
                    StringComparison.Ordinal));
        return true;
    }

    private static void TryCompleteAdoption(
        AgentAttachmentTransferService transferService,
        AgentAttachmentTransferAdoption adoption)
    {
        try
        {
            transferService.CompleteAdoption(adoption);
        }
        catch
        {
            // The durable attachment copy is authoritative after a committed admission.
        }
    }

    private static string Require(string? value, string name)
        => string.IsNullOrWhiteSpace(value)
            ? throw new AgentUserTurnAdmissionException($"{name} is required.")
            : value.Trim();

    private enum AdmissionFailureDurability
    {
        Unknown = 0,
        NotCommitted = 1,
        Committed = 2,
    }
}

internal sealed class AgentUserTurnAdmissionException(string message)
    : InvalidOperationException(message);
