using Avalonia.Controls;
using Avalonia.VisualTree;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentChatView
{
    private IEnumerable<(object Item, Control Visual)> EnumerateRealizedTranscriptAnchors()
        => TranscriptRepeaterAnchors.Enumerate(TranscriptItemsControl, ViewModel?.TranscriptItems);

    private Control? RealizeTranscriptAnchor(object anchorKey)
    {
        anchorKey = ViewModel?.RevealTranscriptAnchor(anchorKey) ?? anchorKey;
        return RealizeTranscriptAnchorCore(anchorKey, bringIntoView: true);
    }

    private Control? RealizePreservedTranscriptAnchor(object anchorKey)
        => RealizeTranscriptAnchorCore(ViewModel?.ResolveVisibleTranscriptAnchor(anchorKey) ?? anchorKey, bringIntoView: false);

    private Control? RealizeTranscriptAnchorCore(object anchorKey, bool bringIntoView)
    {
        var messages = ViewModel?.TranscriptItems;
        if (messages is null)
        {
            return null;
        }

        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index] is ITranscriptAnchorItem anchorItem
                && Equals(anchorItem.AnchorKey, anchorKey))
            {
                return RealizeTranscriptElement(index, bringIntoView);
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
        if (transcriptItems[tailIndex] is not ITranscriptAnchorItem
        {
            AnchorRole: TranscriptAnchorItemRole.TailSentinel,
        })
        {
            return null;
        }

        var tail = RealizeTranscriptElement(tailIndex);
        // A cached tail can have valid geometry outside the viewport. Tail
        // placement is navigation, unlike preserved reading-row realization.
        tail?.BringIntoView();
        TranscriptItemsControl.UpdateLayout();
        return tail;
    }

    private Control? RealizeTranscriptElement(int index, bool bringIntoView = true)
    {
        var realized = TranscriptItemsControl.TryGetElement(index);
        if (realized is not null
            && TranscriptRowPresenter.HasLayoutGeometry(realized))
        {
            return realized;
        }

        // A focused row may be pinned but no longer held by the layout. Suggest
        // it to the repeater before GetOrCreateElement can mistake it for an
        // already-laid-out element and retain its pinned ownership.
        if (realized is not null
            && ViewModel?.TranscriptItems[index] is ITranscriptAnchorItem anchor)
        {
            using (TranscriptAnchorHost.SuspendAnchoring(() => anchor.AnchorKey))
            {
                TranscriptItemsControl.UpdateLayout();
            }
        }

        var element = TranscriptItemsControl.GetOrCreateElement(index) as Control;
        if (element is null)
        {
            return null;
        }

        TranscriptItemsControl.UpdateLayout();
        using (bringIntoView ? null : TranscriptAnchorHost.SuppressPreservedRealizationNavigation())
        {
            element.BringIntoView();
        }
        TranscriptItemsControl.UpdateLayout();
        return TranscriptItemsControl.TryGetElement(index) as Control ?? element;
    }
}
