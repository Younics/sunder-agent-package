using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    [RelayCommand]
    private async Task StartRollbackFromMessageAsync(AgentTextTranscriptRowViewModel? message)
    {
        var selectedSession = SelectedSession;
        if (selectedSession is null || message?.IsUser != true)
        {
            return;
        }

        var turn = _transcriptPageGateway is null
            ? _sessionService.GetTurn(message.RowId)
            : (await _transcriptPageGateway.LoadTranscriptPageAsync(
                    new Runtime.AgentTranscriptPageRequest(
                        selectedSession.SessionId,
                        Runtime.AgentTranscriptPageDirection.Turn,
                        1,
                        AnchorTurnId: message.RowId))
                .ConfigureAwait(false)).Turns.FirstOrDefault();
        if (turn is null || turn.SessionId != selectedSession.SessionId)
        {
            await InvokeOnUiThreadAsync(() =>
                ApplySessionStatus(selectedSession, "The selected message is no longer available."))
                .ConfigureAwait(false);
            return;
        }

        if (turn.Role != AgentMessageRole.User || turn.Kind != AgentTurnKind.Message)
        {
            await InvokeOnUiThreadAsync(() =>
                ApplySessionStatus(selectedSession, "Only user messages can be edited from history."))
                .ConfigureAwait(false);
            return;
        }

        var attachmentUploads = await LoadRollbackAttachmentUploadsAsync(turn).ConfigureAwait(false);
        await InvokeOnUiThreadAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            ClearPendingAttachments();
            PendingRollbackTurnId = turn.TurnId;
            DraftMessage = TranscriptRowProjector<AgentTranscriptRowViewModel>.ExtractTextContent(turn);
            selectedSession.DraftMessage = DraftMessage;
            foreach (var upload in attachmentUploads.Uploads)
            {
                TryAddAttachmentUpload(upload);
            }

            var status = attachmentUploads.SkippedCount == 0
                ? "Editing an earlier message. Send will replace this point in the transcript."
                : $"Editing an earlier message. {attachmentUploads.SkippedCount} attachment(s) could not be restored.";
            ApplySessionStatus(selectedSession, status);
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private void CancelRollback()
    {
        ClearRollbackStateOnly();
        DraftMessage = string.Empty;
        if (SelectedSession is { } selectedSession)
        {
            selectedSession.DraftMessage = string.Empty;
            ApplySessionStatus(selectedSession, "Rollback canceled.");
        }

        ClearPendingAttachments();
    }

    private void ClearRollbackStateOnly()
        => PendingRollbackTurnId = null;

    private async Task<RollbackAttachmentUploads> LoadRollbackAttachmentUploadsAsync(AgentTurnRecord turn)
    {
        var metadata = turn.Items
            .Where(item => item.Kind == AgentTurnItemKind.Attachment)
            .Select(TryReadAttachmentMetadata)
            .Where(item => item is not null)
            .Cast<AgentAttachmentMetadata>()
            .ToArray();
        if (metadata.Length == 0)
        {
            return new RollbackAttachmentUploads([], 0);
        }

        if (_attachmentService is null)
        {
            return new RollbackAttachmentUploads([], metadata.Length);
        }

        var uploads = new List<AgentAttachmentUploadRequest>(metadata.Length);
        var skipped = 0;
        foreach (var attachment in metadata)
        {
            try
            {
                var content = await _attachmentService.ReadAttachmentBytesAsync(attachment).ConfigureAwait(false);
                uploads.Add(new AgentAttachmentUploadRequest(attachment.FileName, attachment.MediaType, content));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FileNotFoundException)
            {
                skipped++;
            }
        }

        return new RollbackAttachmentUploads(uploads, skipped);
    }

    private static AgentAttachmentMetadata? TryReadAttachmentMetadata(AgentTurnItemRecord item)
    {
        if (string.IsNullOrWhiteSpace(item.StructuredPayloadJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentAttachmentMetadata>(item.StructuredPayloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record RollbackAttachmentUploads(
        IReadOnlyList<AgentAttachmentUploadRequest> Uploads,
        int SkippedCount);
}
