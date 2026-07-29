using System.Security.Cryptography;
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
        catch
        {
            _attachmentStore.Cleanup(stored);
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
        var committed = false;
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
            committed = true;
            transferService.CompleteAdoption(adoption);
            if (result.IsExisting)
            {
                _attachmentStore.Cleanup(stored);
            }
            return result;
        }
        catch
        {
            _attachmentStore.Cleanup(stored);
            if (!committed)
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
            throw new ArgumentException("User turn id cannot be empty.", nameof(userTurnId));
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

        AgentUserTurnAdmissionResult result;
        using (await _transitionGate.EnterAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            var session = _sessionService.GetSession(sessionId)
                ?? throw new InvalidOperationException($"Session '{sessionId}' was deleted before admission.");
            if (_deletionFence.IsFenced(session))
            {
                throw new InvalidOperationException("The session is being deleted and cannot admit a new run.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            result = _sessionService.AdmitUserTurn(request);
            if (!result.IsExisting)
            {
                CancelSupersededProcessRun(sessionId, result.Run.Key);
            }
        }

        return result;
    }

    private void ValidateSessionBeforeAdoption(Guid sessionId)
    {
        var session = _sessionService.GetSession(sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' was not found.");
        if (_deletionFence.IsFenced(session))
        {
            throw new InvalidOperationException("The session is being deleted and cannot admit a new run.");
        }
    }

    private void CancelSupersededProcessRun(Guid sessionId, AgentDurableRunKey admittedRun)
    {
        var active = _activeRunRegistry.Remove(sessionId);
        if (active is null
            || active.RunId == admittedRun.RunId
               && active.RunRevision == admittedRun.RunRevision)
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

    private static string Require(string? value, string name)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} is required.")
            : value.Trim();
}
