using Avalonia.Controls;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public partial class SubsessionsView
{
    private IEnumerable<(object Item, Control Visual)> EnumerateRealizedTranscriptAnchors()
    {
        var transcriptItems = ViewModel?.TranscriptItems;
        if (transcriptItems is null)
        {
            yield break;
        }

        for (var index = 0; index < transcriptItems.Count; index++)
        {
            if (transcriptItems[index] is ITranscriptAnchorItem
                {
                    AnchorRole: TranscriptAnchorItemRole.Transient,
                } anchorItem
                && TranscriptItemsControl.TryGetElement(index) is Control visual)
            {
                yield return (anchorItem.AnchorKey, visual);
            }
        }
    }

    private Control? RealizeTranscriptAnchor(object anchorKey)
    {
        var messages = ViewModel?.Messages;
        if (messages is null)
        {
            return null;
        }

        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index] is ITranscriptAnchorItem anchorItem
                && Equals(anchorItem.AnchorKey, anchorKey))
            {
                return RealizeTranscriptElement(index);
            }
        }

        return null;
    }

    private Control? RealizeTranscriptTailSentinel()
    {
        var transcriptItems = ViewModel?.TranscriptItems;
        if (transcriptItems is null || transcriptItems.Count == 0)
        {
            return null;
        }

        var tailIndex = transcriptItems.Count - 1;
        return transcriptItems[tailIndex] is ITranscriptAnchorItem
        {
            AnchorRole: TranscriptAnchorItemRole.TailSentinel,
        }
            ? RealizeTranscriptElement(tailIndex)
            : null;
    }

    private Control? RealizeTranscriptElement(int index)
    {
        if (TranscriptItemsControl.TryGetElement(index) is Control realized)
        {
            return realized;
        }

        var element = TranscriptItemsControl.GetOrCreateElement(index) as Control;
        if (element is null)
        {
            return null;
        }

        TranscriptItemsControl.UpdateLayout();
        element.BringIntoView();
        TranscriptItemsControl.UpdateLayout();
        return TranscriptItemsControl.TryGetElement(index) as Control ?? element;
    }
}
