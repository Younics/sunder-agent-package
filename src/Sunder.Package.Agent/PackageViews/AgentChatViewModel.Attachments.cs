using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentChatViewModel
{
    public ObservableCollection<AgentPendingAttachmentViewModel> PendingAttachments { get; } = [];

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

    public async Task AddAttachmentPathsAsync(IReadOnlyList<string> paths)
    {
        if (!CanAcceptAttachments())
        {
            return;
        }

        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                var upload = await _attachmentService!.LoadUploadRequestFromFileAsync(path);
                if (!TryAddAttachmentUpload(upload))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                SetAttachmentStatus(ex.Message);
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
        if (PendingAttachments.Count >= AgentAttachmentService.MaxAttachmentsPerMessage)
        {
            SetAttachmentStatus($"A message can include at most {AgentAttachmentService.MaxAttachmentsPerMessage} attachments.");
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
    private async Task OpenTranscriptAttachmentPreviewAsync(AgentTranscriptAttachmentViewModel? attachment)
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

        try
        {
            var content = await _attachmentService.ReadAttachmentBytesAsync(attachment.Metadata);
            ShowAttachmentPreviewImage(attachment.FileName, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetGlobalStatus(ex.Message);
        }
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
