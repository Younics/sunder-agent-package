using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

internal sealed class AgentTranscriptRowFactory(
    AgentToolPresentationService toolPresentationService,
    Func<AgentTranscriptToolDetailRequest, CancellationToken, Task<AgentTranscriptToolDetailRecord?>> loadToolDetail,
    IActivityTicker activityTicker,
    Func<AgentTurnRecord, string> senderNameResolver,
    Func<AgentTurnRecord, AgentTurnItemRecord, IReadOnlyList<AgentChildSessionLinkViewModel>> childSessionLinksResolver)
    : ITranscriptRowFactory<AgentTranscriptRowViewModel>
{
    public AgentTranscriptRowViewModel? CreateMessage(
        AgentTurnRecord turn,
        TranscriptMessageProjection projection)
    {
        var attachments = ExtractAttachments(turn);
        return string.IsNullOrWhiteSpace(projection.Content) && attachments.Count == 0
            ? null
            : new AgentTextTranscriptRowViewModel(
                turn,
                projection.Content,
                attachments,
                senderNameResolver(turn));
    }

    public void UpdateMessage(
        AgentTranscriptRowViewModel row,
        AgentTurnRecord turn,
        TranscriptMessageProjection projection)
    {
        if (row is AgentTextTranscriptRowViewModel textRow)
        {
            textRow.UpdateContent(projection.Content);
            textRow.ReplaceAttachments(ExtractAttachments(turn));
        }
    }

    public AgentTranscriptRowViewModel CreateTool(
        AgentTurnRecord turn,
        AgentTurnItemRecord item,
        TranscriptToolProjection projection)
        => new AgentToolInvocationRowViewModel(
            turn,
            item,
            projection,
            toolPresentationService,
            loadToolDetail,
            childSessionLinksResolver);

    public void ApplyToolResult(
        AgentTranscriptRowViewModel row,
        AgentTurnRecord turn,
        AgentTurnItemRecord item,
        TranscriptToolProjection projection)
    {
        if (row is AgentToolInvocationRowViewModel toolRow)
        {
            toolRow.ApplyResult(turn, item, projection);
        }
    }

    public void UpdateTool(
        AgentTranscriptRowViewModel row,
        AgentTurnRecord turn,
        AgentTurnItemRecord item,
        TranscriptToolProjection projection)
    {
        if (row is AgentToolInvocationRowViewModel toolRow)
        {
            toolRow.UpdateProjection(turn, item, projection);
        }
    }

    public AgentTranscriptRowViewModel CreateActivity(TranscriptActivityProjection projection)
        => new AgentActivityTranscriptRowViewModel(activityTicker, projection.Text, projection.IsReasoning);

    public void UpdateActivity(
        AgentTranscriptRowViewModel row,
        TranscriptActivityProjection projection)
    {
        if (row is AgentActivityTranscriptRowViewModel activityRow)
        {
            activityRow.SetActivityTextBase(projection.Text, projection.IsReasoning);
        }
    }

    public Guid GetRowId(AgentTranscriptRowViewModel row) => row.RowId;

    public object GetAnchorKey(AgentTranscriptRowViewModel row) => row.AnchorKey;

    public Guid? GetResultTurnId(AgentTranscriptRowViewModel row)
        => (row as AgentToolInvocationRowViewModel)?.ResultTurnId;

    public void SetExpanded(AgentTranscriptRowViewModel row, bool isExpanded)
    {
        if (row is AgentToolInvocationRowViewModel toolRow)
        {
            if (!isExpanded)
            {
                toolRow.CollapseDetails();
            }
        }
    }

    public void RefreshRelatedRows(AgentTranscriptRowViewModel row)
        => (row as AgentToolInvocationRowViewModel)?.RefreshChildSessionLink();

    public void DisposeRow(AgentTranscriptRowViewModel row)
        => (row as IDisposable)?.Dispose();

    private static IReadOnlyList<AgentTranscriptAttachmentViewModel> ExtractAttachments(
        AgentTurnRecord turn)
        => turn.Items
            .Where(item => item.Kind == AgentTurnItemKind.Attachment)
            .Select(TryCreateAttachment)
            .Where(attachment => attachment is not null)
            .Cast<AgentTranscriptAttachmentViewModel>()
            .ToArray();

    private static AgentTranscriptAttachmentViewModel? TryCreateAttachment(AgentTurnItemRecord item)
    {
        if (string.IsNullOrWhiteSpace(item.StructuredPayloadJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentAttachmentMetadata>(item.StructuredPayloadJson)
                is { } metadata
                ? new AgentTranscriptAttachmentViewModel(metadata)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
