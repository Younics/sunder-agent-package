namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed partial class TranscriptTimelineState<TRow> where TRow : class
{
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _projector.RowsChanging -= OnRowsChanging;
        _projector.RowCreated -= OnRowCreated;
        _initialOperation.Dispose();
        _pageOperation.Dispose();
        _compactRefresh?.Dispose();
        _compact?.Dispose();
        _authoritativeTurnVersions.Clear();
        _projector.Reset();
    }
}
