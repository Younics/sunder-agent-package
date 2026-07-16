using Avalonia.Controls;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentChatView
{
    private IEnumerable<(object Item, Control Visual)> EnumerateRealizedTranscriptAnchors()
    {
        var messages = ViewModel?.Messages;
        if (messages is null)
        {
            yield break;
        }

        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index] is ITranscriptAnchorItem anchorItem
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
            if (Equals(messages[index].AnchorKey, anchorKey))
            {
                return TranscriptItemsControl.GetOrCreateElement(index);
            }
        }

        return null;
    }
}
