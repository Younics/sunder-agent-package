using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal static class TranscriptRepeaterAnchors
{
    internal static IEnumerable<(object Item, Control Visual)> Enumerate<TRow>(
        ItemsRepeater repeater, IReadOnlyList<TRow>? items) where TRow : class
    {
        if (items is null) yield break;
        // GetElementIndex reads the child's virtualization metadata. In contrast,
        // TryGetElement searches the children, so calling it for every loaded row
        // multiplies history size by the repeater's realized/recycled pool size.
        foreach (var visual in repeater.GetVisualChildren().OfType<Control>())
        {
            var index = repeater.GetElementIndex(visual);
            if (index >= 0 && index < items.Count
                && ReferenceEquals(items[index], visual.DataContext)
                && visual.DataContext is ITranscriptAnchorItem { AnchorRole: TranscriptAnchorItemRole.Transient } anchor
                && TranscriptRowPresenter.HasLayoutGeometry(visual))
                yield return (anchor.AnchorKey, visual);
        }
    }
}
