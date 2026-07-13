using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Subagents.PackageViews;

internal sealed class SubsessionTranscriptRowFactory(
    TranscriptToolPresentationService toolPresentationService,
    IActivityTicker activityTicker,
    Func<AgentTurnRecord, AgentTurnItemRecord, IReadOnlyList<SubsessionChildSessionLinkViewModel>> childSessionLinksResolver)
    : ITranscriptRowFactory<SubsessionTranscriptRowViewModel>
{
    public SubsessionTranscriptRowViewModel? CreateMessage(
        AgentTurnRecord turn,
        TranscriptMessageProjection projection)
        => string.IsNullOrWhiteSpace(projection.Content)
            ? null
            : new SubsessionTextTranscriptRowViewModel(turn, projection.Content);

    public void UpdateMessage(
        SubsessionTranscriptRowViewModel row,
        AgentTurnRecord turn,
        TranscriptMessageProjection projection)
    {
        if (row is SubsessionTextTranscriptRowViewModel textRow)
        {
            textRow.UpdateContent(projection.Content);
        }
    }

    public SubsessionTranscriptRowViewModel CreateTool(
        AgentTurnRecord turn,
        AgentTurnItemRecord item,
        TranscriptToolProjection projection)
        => new SubsessionToolInvocationRowViewModel(
            turn,
            item,
            toolPresentationService,
            childSessionLinksResolver);

    public void ApplyToolResult(
        SubsessionTranscriptRowViewModel row,
        AgentTurnRecord turn,
        AgentTurnItemRecord item,
        TranscriptToolProjection projection)
    {
        if (row is SubsessionToolInvocationRowViewModel toolRow)
        {
            toolRow.ApplyResult(turn, item);
        }
    }

    public SubsessionTranscriptRowViewModel CreateActivity(TranscriptActivityProjection projection)
        => new SubsessionActivityTranscriptRowViewModel(activityTicker, projection.Text);

    public void UpdateActivity(
        SubsessionTranscriptRowViewModel row,
        TranscriptActivityProjection projection)
    {
        if (row is SubsessionActivityTranscriptRowViewModel activityRow)
        {
            activityRow.SetActivityTextBase(projection.Text);
        }
    }

    public Guid GetRowId(SubsessionTranscriptRowViewModel row) => row.RowId;

    public object GetAnchorKey(SubsessionTranscriptRowViewModel row) => row.AnchorKey;

    public Guid? GetResultTurnId(SubsessionTranscriptRowViewModel row)
        => (row as SubsessionToolInvocationRowViewModel)?.ResultTurnId;

    public void SetExpanded(SubsessionTranscriptRowViewModel row, bool isExpanded)
    {
        if (row is SubsessionToolInvocationRowViewModel toolRow)
        {
            toolRow.IsExpanded = isExpanded;
        }
    }

    public void RefreshRelatedRows(SubsessionTranscriptRowViewModel row)
        => (row as SubsessionToolInvocationRowViewModel)?.RefreshChildSessionLink();

    public void DisposeRow(SubsessionTranscriptRowViewModel row)
        => (row as IDisposable)?.Dispose();
}
