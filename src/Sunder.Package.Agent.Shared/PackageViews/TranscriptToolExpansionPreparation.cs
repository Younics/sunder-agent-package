using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal static class TranscriptToolExpansionPreparation
{
    public static bool IsCurrent(
        Control header,
        object row,
        TranscriptRowPresenter scope,
        TranscriptToolDetailHost detailHost,
        object anchorKey,
        Func<bool> isStateCurrent)
        => header.IsAttachedToVisualTree()
           && scope.IsAttachedToVisualTree()
           && detailHost.IsAttachedToVisualTree()
           && ReferenceEquals(header.DataContext, row)
           && ReferenceEquals(scope.DataContext, row)
           && ReferenceEquals(detailHost.DataContext, row)
           && ReferenceEquals(
               header.GetVisualAncestors().OfType<TranscriptRowPresenter>().FirstOrDefault(),
               scope)
           && scope.IsVisualAncestorOf(detailHost)
           && scope.AnchorRole == TranscriptAnchorItemRole.Transient
           && Equals(scope.AnchorKey, anchorKey)
           && isStateCurrent();
}
