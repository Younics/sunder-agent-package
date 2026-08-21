using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    public ObservableCollection<AgentPendingAttachmentViewModel> PendingAttachments
        => _composer.Attachments;

    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    public string PendingAttachmentSummaryText => PendingAttachments.Count == 1
        ? "1 file attached"
        : $"{PendingAttachments.Count} files attached";

    [ObservableProperty]
    private bool _isComposerDropTargetActive;

    [ObservableProperty]
    private bool _isAttachmentPreviewVisible;

    [ObservableProperty]
    private Bitmap? _attachmentPreviewImage;

    [ObservableProperty]
    private string _attachmentPreviewTitle = string.Empty;

    public bool HasAttachmentPreviewImage => AttachmentPreviewImage is not null;

    partial void OnAttachmentPreviewImageChanged(Bitmap? value)
        => OnPropertyChanged(nameof(HasAttachmentPreviewImage));

    public async Task AddAttachmentPathsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        if (!CanAcceptAttachments())
        {
            return;
        }

        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                var upload = await _attachmentService!.LoadUploadRequestFromFileAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var added = false;
                await InvokeOnUiThreadAsync(() => added = TryAddAttachmentUpload(upload))
                    .ConfigureAwait(false);
                if (!added)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                await InvokeOnUiThreadAsync(() => SetAttachmentStatus(ex.Message)).ConfigureAwait(false);
            }
        }
    }

    public Task AddAttachmentUploadsAsync(IReadOnlyList<AgentAttachmentUploadRequest> uploads)
    {
        if (!CanAcceptAttachments())
        {
            return Task.CompletedTask;
        }

        foreach (var upload in uploads)
        {
            if (!TryAddAttachmentUpload(upload))
            {
                return Task.CompletedTask;
            }
        }

        return Task.CompletedTask;
    }

    private bool CanAcceptAttachments()
    {
        if (_attachmentService is null)
        {
            SetAttachmentStatus("Attachment support is unavailable in this host.");
            return false;
        }

        if (SelectedSession is null)
        {
            SetAttachmentStatus("Create or select a session before attaching files.");
            return false;
        }

        if (!IsSelectedSessionRunInactive)
        {
            SetAttachmentStatus("Wait for the current run to finish before attaching files.");
            return false;
        }

        return true;
    }

    private bool TryAddAttachmentUpload(AgentAttachmentUploadRequest upload)
    {
        if (PendingAttachments.Count >= AgentAttachmentUploadRules.MaxAttachmentsPerMessage)
        {
            SetAttachmentStatus($"A message can include at most {AgentAttachmentUploadRules.MaxAttachmentsPerMessage} attachments.");
            return false;
        }

        try
        {
            var info = _attachmentService!.InspectUpload(upload);
            PendingAttachments.Add(new AgentPendingAttachmentViewModel(Guid.NewGuid(), upload, info));
            return true;
        }
        catch (InvalidOperationException ex)
        {
            SetAttachmentStatus(ex.Message);
            return true;
        }
    }

    [RelayCommand]
    private void RemovePendingAttachment(AgentPendingAttachmentViewModel? attachment)
    {
        if (attachment is not null)
        {
            PendingAttachments.Remove(attachment);
        }
    }

    [RelayCommand]
    private void OpenPendingAttachmentPreview(AgentPendingAttachmentViewModel? attachment)
    {
        if (attachment?.IsPreviewable != true)
        {
            return;
        }

        ShowAttachmentPreviewImage(attachment.FileName, attachment.UploadRequest.Content);
    }

    [RelayCommand]
    private Task OpenTranscriptAttachmentPreviewAsync(AgentTranscriptAttachmentViewModel? attachment)
        => RunOperationAsync(
            AttachmentPreviewOperation,
            cancellationToken => OpenTranscriptAttachmentPreviewCoreAsync(attachment, cancellationToken),
            _ => InvokeOnUiThreadAsync(CloseAttachmentPreview));

    private async Task OpenTranscriptAttachmentPreviewCoreAsync(
        AgentTranscriptAttachmentViewModel? attachment,
        CancellationToken cancellationToken)
    {
        if (attachment?.IsPreviewable != true)
        {
            return;
        }

        if (_attachmentService is null)
        {
            SetGlobalStatus("Attachment previews are unavailable in this host.");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] content;
        try
        {
            content = await _attachmentService.ReadAttachmentBytesAsync(attachment.Metadata)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (!PresentationExceptionPolicy.IsFatal(exception))
        {
            throw new PresentationOperationException(
                "The attachment content could not be read.",
                exception);
        }
        await InvokeOnUiThreadAsync(() => ShowAttachmentPreviewImage(attachment.FileName, content))
            .ConfigureAwait(false);
    }

    [RelayCommand]
    private void CloseAttachmentPreview()
    {
        IsAttachmentPreviewVisible = false;
        AttachmentPreviewTitle = string.Empty;
        DisposeAttachmentPreviewImage();
    }

    private void ClearPendingAttachments()
    {
        if (PendingAttachments.Count > 0)
        {
            PendingAttachments.Clear();
        }
    }

    private void OnPendingAttachmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPendingAttachments));
        OnPropertyChanged(nameof(PendingAttachmentSummaryText));
        SendMessageCommand.NotifyCanExecuteChanged();
        ClearComposerCommand.NotifyCanExecuteChanged();
    }

    private void ShowAttachmentPreviewImage(string fileName, byte[] content)
    {
        Bitmap previewImage;
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            previewImage = new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or IOException or NotSupportedException)
        {
            SetGlobalStatus($"Unable to preview '{fileName}' as an image.");
            return;
        }

        var oldImage = AttachmentPreviewImage;
        AttachmentPreviewTitle = fileName;
        AttachmentPreviewImage = previewImage;
        IsAttachmentPreviewVisible = true;
        oldImage?.Dispose();
    }

    private void DisposeAttachmentPreviewImage()
    {
        var oldImage = AttachmentPreviewImage;
        AttachmentPreviewImage = null;
        oldImage?.Dispose();
    }

    private void SetAttachmentStatus(string statusText)
    {
        if (SelectedSession is { } selectedSession)
        {
            ApplySessionStatus(selectedSession, statusText);
            return;
        }

        SetGlobalStatus(statusText);
    }
}
