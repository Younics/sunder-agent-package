using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.PackageViews;

public sealed class AgentPendingAttachmentViewModel(
    Guid attachmentId,
    AgentAttachmentUploadRequest uploadRequest,
    AgentAttachmentInfo info) : ObservableObject
{
    public Guid AttachmentId { get; } = attachmentId;

    public AgentAttachmentUploadRequest UploadRequest { get; } = uploadRequest;

    public string FileName => info.FileName;

    public string KindLabel => FormatAttachmentKind(info.Kind);

    public string BadgeText => FormatAttachmentBadge(info.Kind);

    public bool IsPreviewable => AgentAttachmentPreviewSupport.IsPreviewableImage(info.Kind, info.MediaType);

    public string PreviewToolTipText => IsPreviewable ? "Preview image" : "Preview unavailable";

    public string DetailText => info.WasTruncated
        ? $"{KindLabel} - {FormatByteCount(info.SizeBytes)} - truncated"
        : $"{KindLabel} - {FormatByteCount(info.SizeBytes)}";

    private static string FormatAttachmentKind(AgentAttachmentKind kind)
        => kind switch
        {
            AgentAttachmentKind.Image => "Image",
            AgentAttachmentKind.Pdf => "PDF",
            AgentAttachmentKind.Audio => "Audio",
            AgentAttachmentKind.Video => "Video",
            AgentAttachmentKind.Text => "Text",
            _ => "File",
        };

    private static string FormatAttachmentBadge(AgentAttachmentKind kind)
        => kind switch
        {
            AgentAttachmentKind.Image => "IMG",
            AgentAttachmentKind.Pdf => "PDF",
            AgentAttachmentKind.Audio => "AUD",
            AgentAttachmentKind.Video => "VID",
            AgentAttachmentKind.Text => "TXT",
            _ => "BIN",
        };

    private static string FormatByteCount(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.#} MB"
            : bytes >= 1024
                ? $"{bytes / 1024d:0.#} KB"
                : $"{bytes} B";
}

public sealed class AgentTranscriptAttachmentViewModel(AgentAttachmentMetadata metadata) : ObservableObject
{
    public AgentAttachmentMetadata Metadata { get; } = metadata;

    public string FileName => Metadata.FileName;

    public string MediaType => Metadata.MediaType;

    public string KindLabel => FormatAttachmentKind(Metadata.Kind);

    public string BadgeText => FormatAttachmentBadge(Metadata.Kind);

    public bool IsPreviewable => AgentAttachmentPreviewSupport.IsPreviewableImage(Metadata.Kind, Metadata.MediaType);

    public string PreviewToolTipText => IsPreviewable ? "Preview image" : "Preview unavailable";

    public string DetailText => Metadata.WasTruncated
        ? $"{KindLabel} - {FormatByteCount(Metadata.SizeBytes)} - truncated"
        : $"{KindLabel} - {FormatByteCount(Metadata.SizeBytes)}";

    private static string FormatAttachmentKind(AgentAttachmentKind kind)
        => kind switch
        {
            AgentAttachmentKind.Image => "Image",
            AgentAttachmentKind.Pdf => "PDF",
            AgentAttachmentKind.Audio => "Audio",
            AgentAttachmentKind.Video => "Video",
            AgentAttachmentKind.Text => "Text",
            _ => "File",
        };

    private static string FormatAttachmentBadge(AgentAttachmentKind kind)
        => kind switch
        {
            AgentAttachmentKind.Image => "IMG",
            AgentAttachmentKind.Pdf => "PDF",
            AgentAttachmentKind.Audio => "AUD",
            AgentAttachmentKind.Video => "VID",
            AgentAttachmentKind.Text => "TXT",
            _ => "BIN",
        };

    private static string FormatByteCount(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.#} MB"
            : bytes >= 1024
                ? $"{bytes / 1024d:0.#} KB"
                : $"{bytes} B";
}

internal static class AgentAttachmentPreviewSupport
{
    public static bool IsPreviewableImage(AgentAttachmentKind kind, string mediaType)
        => kind == AgentAttachmentKind.Image
            && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mediaType, "image/svg+xml", StringComparison.OrdinalIgnoreCase);
}
