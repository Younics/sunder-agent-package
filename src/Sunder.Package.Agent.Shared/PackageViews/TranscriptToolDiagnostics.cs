using System.Diagnostics;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal static class TranscriptToolDiagnostics
{
    private static long _headersProjected;
    private static long _detailLoads;
    private static long _detailLoadFailures;
    private static long _detailLoadCancellations;
    private static long _resolverCalls;
    private static long _cacheHits = 0;
    private static long _cacheMisses;
    private static long _cacheEvictions = 0;
    private static long _activeHeaderViewModels;
    private static long _activeDetailViewModels;
    private static long _activeDetailVisuals;
    private static long _activeMarkdownRenderers;
    private static long _markdownParses;
    private static long _markdownApplies;
    private static long _activeDiffLines;
    private static long _spacerActivations;
    private static long _spacerReleases;
    private static long _spacerLifetimeTimestampTicks;
    private static long _collapseClamps;
    private static long _maximumHeaderDisplacementMilliPixels;

    internal static TranscriptToolDiagnosticSnapshot Snapshot => new(
        Interlocked.Read(ref _headersProjected),
        Interlocked.Read(ref _detailLoads),
        Interlocked.Read(ref _detailLoadFailures),
        Interlocked.Read(ref _detailLoadCancellations),
        Interlocked.Read(ref _resolverCalls),
        Interlocked.Read(ref _cacheHits),
        Interlocked.Read(ref _cacheMisses),
        Interlocked.Read(ref _cacheEvictions),
        Interlocked.Read(ref _activeHeaderViewModels),
        Interlocked.Read(ref _activeDetailViewModels),
        Interlocked.Read(ref _activeDetailVisuals),
        Interlocked.Read(ref _activeMarkdownRenderers),
        Interlocked.Read(ref _markdownParses),
        Interlocked.Read(ref _markdownApplies),
        Interlocked.Read(ref _activeDiffLines),
        Interlocked.Read(ref _spacerActivations),
        Interlocked.Read(ref _spacerReleases),
        Interlocked.Read(ref _spacerLifetimeTimestampTicks),
        Interlocked.Read(ref _collapseClamps),
        Interlocked.Read(ref _maximumHeaderDisplacementMilliPixels) / 1000d);

    internal static void HeaderProjected() => Interlocked.Increment(ref _headersProjected);
    internal static void DetailLoadStarted()
    {
        Interlocked.Increment(ref _detailLoads);
        Interlocked.Increment(ref _cacheMisses);
    }
    internal static void DetailLoadFailed() => Interlocked.Increment(ref _detailLoadFailures);
    internal static void DetailLoadCanceled() => Interlocked.Increment(ref _detailLoadCancellations);
    internal static void ResolverCalled() => Interlocked.Increment(ref _resolverCalls);
    internal static void HeaderViewModelCreated() => Interlocked.Increment(ref _activeHeaderViewModels);
    internal static void HeaderViewModelDestroyed() => Interlocked.Decrement(ref _activeHeaderViewModels);
    internal static void DetailViewModelCreated(int diffLines)
    {
        Interlocked.Increment(ref _activeDetailViewModels);
        Interlocked.Add(ref _activeDiffLines, diffLines);
    }
    internal static void DetailViewModelDestroyed(int diffLines)
    {
        Interlocked.Decrement(ref _activeDetailViewModels);
        Interlocked.Add(ref _activeDiffLines, -diffLines);
    }
    internal static void DetailVisualCreated() => Interlocked.Increment(ref _activeDetailVisuals);
    internal static void DetailVisualDestroyed() => Interlocked.Decrement(ref _activeDetailVisuals);
    internal static void MarkdownRendererCreated() => Interlocked.Increment(ref _activeMarkdownRenderers);
    internal static void MarkdownRendererDestroyed() => Interlocked.Decrement(ref _activeMarkdownRenderers);
    internal static void MarkdownParsed() => Interlocked.Increment(ref _markdownParses);
    internal static void MarkdownApplied() => Interlocked.Increment(ref _markdownApplies);
    internal static long SpacerActivated()
    {
        Interlocked.Increment(ref _spacerActivations);
        return Stopwatch.GetTimestamp();
    }
    internal static void SpacerReleased(long startedAt)
    {
        Interlocked.Increment(ref _spacerReleases);
        if (startedAt > 0)
        {
            Interlocked.Add(ref _spacerLifetimeTimestampTicks, Math.Max(0, Stopwatch.GetTimestamp() - startedAt));
        }
    }
    internal static void CollapseClamped() => Interlocked.Increment(ref _collapseClamps);
    internal static void ObserveHeaderDisplacement(double pixels)
    {
        var value = double.IsFinite(pixels)
            ? checked((long)Math.Round(Math.Abs(pixels) * 1000))
            : 0;
        while (true)
        {
            var current = Interlocked.Read(ref _maximumHeaderDisplacementMilliPixels);
            if (value <= current
                || Interlocked.CompareExchange(ref _maximumHeaderDisplacementMilliPixels, value, current) == current)
            {
                return;
            }
        }
    }
}

internal readonly record struct TranscriptToolDiagnosticSnapshot(
    long HeadersProjected,
    long DetailLoads,
    long DetailLoadFailures,
    long DetailLoadCancellations,
    long ResolverCalls,
    long CacheHits,
    long CacheMisses,
    long CacheEvictions,
    long ActiveHeaderViewModels,
    long ActiveDetailViewModels,
    long ActiveDetailVisuals,
    long ActiveMarkdownRenderers,
    long MarkdownParses,
    long MarkdownApplies,
    long ActiveDiffLines,
    long SpacerActivations,
    long SpacerReleases,
    long SpacerLifetimeTimestampTicks,
    long CollapseClamps,
    double MaximumHeaderDisplacement);
