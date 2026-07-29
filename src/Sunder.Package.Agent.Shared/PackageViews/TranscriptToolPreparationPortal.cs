using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed class TranscriptToolPreparationPortal : Panel, IDisposable
{
    private ContentPresenter? _presenter;
    private CancellationTokenSource? _preparationCancellation;
    private long _generation;
    private bool _disposed;

    internal async Task<PreparedTranscriptToolVisual> PrepareAsync(
        object content,
        IDataTemplate contentTemplate,
        double width,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(contentTemplate);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Dispatcher.UIThread.VerifyAccess();
        CancelPreparation();

        var generation = ++_generation;
        var currentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var currentToken = currentCancellation.Token;
        _preparationCancellation = currentCancellation;
        var presenter = new ContentPresenter
        {
            Content = content,
            ContentTemplate = contentTemplate,
            Width = Math.Max(1, width),
            MinWidth = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Opacity = 0,
            IsHitTestVisible = false,
            Focusable = false,
        };
        StreamingMarkdownPresenter.SetIsPreparingToolDetail(presenter, true);
        _presenter = presenter;
        Children.Add(presenter);
        TranscriptToolDiagnostics.DetailVisualCreated();
        InvalidateMeasure();

        PreparationFingerprint? previous = null;
        var stablePasses = 0;
        try
        {
            for (var pass = 0; pass < 256; pass++)
            {
                currentToken.ThrowIfCancellationRequested();
                Dispatcher.UIThread.VerifyAccess();
                presenter.InvalidateMeasure();
                InvalidateMeasure();
                await Task.Delay(1, currentToken);
                currentToken.ThrowIfCancellationRequested();
                Dispatcher.UIThread.VerifyAccess();
                if (!IsCurrent(generation, presenter))
                {
                    throw new OperationCanceledException(currentToken);
                }

                var sources = presenter.GetVisualDescendants()
                    .OfType<ITranscriptGeometrySource>()
                    .Where(source => source is not Visual visual || visual.IsEffectivelyVisible)
                    .ToArray();
                var pending = sources.Any(source => source.IsGeometryPending);
                var fingerprint = new PreparationFingerprint(
                    Quantize(presenter.DesiredSize.Width),
                    Quantize(presenter.DesiredSize.Height),
                    sources.Aggregate(17L, static (value, source) => unchecked(
                        value * 31
                        + source.RequestedRevision * 17
                        + source.SettledRevision * 7
                        + source.GeometryRevision)));
                stablePasses = !pending && presenter.Child is not null && previous == fingerprint
                    ? stablePasses + 1
                    : 0;
                previous = fingerprint;
                if (stablePasses >= 2)
                {
                    return new PreparedTranscriptToolVisual(
                        this,
                        generation,
                        content,
                        presenter,
                        Math.Max(0, presenter.DesiredSize.Height));
                }

            }

            throw new TimeoutException("Tool detail geometry did not settle before the preparation budget expired.");
        }
        catch
        {
            if (IsCurrent(generation, presenter))
            {
                DestroyOwnedPresenter();
            }
            throw;
        }
    }

    internal bool Commit(
        PreparedTranscriptToolVisual prepared,
        TranscriptToolDetailHost host,
        ITranscriptToolExpansionOwner owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!ReferenceEquals(prepared.Portal, this)
            || !IsCurrent(prepared.Generation, prepared.Presenter))
        {
            return false;
        }

        var markdownPresenters = prepared.Presenter.GetVisualDescendants()
            .OfType<StreamingMarkdownPresenter>()
            .ToArray();
        foreach (var markdown in markdownPresenters)
        {
            markdown.PreserveRenderedContentOnDetach = true;
        }
        try
        {
            StreamingMarkdownPresenter.SetIsPreparingToolDetail(prepared.Presenter, false);
            prepared.Presenter.Opacity = 1;
            prepared.Presenter.IsHitTestVisible = true;
            Children.Remove(prepared.Presenter);
            _presenter = null;
            Interlocked.Exchange(ref _preparationCancellation, null)?.Dispose();
            host.AttachPrepared(owner, prepared.Presenter);
        }
        finally
        {
            foreach (var markdown in markdownPresenters)
            {
                markdown.PreserveRenderedContentOnDetach = false;
            }
        }
        return true;
    }

    internal void CancelPreparation()
    {
        _generation++;
        var cancellation = Interlocked.Exchange(ref _preparationCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        DestroyOwnedPresenter();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_presenter is not { } presenter)
        {
            return new Size(0, 0);
        }
        var width = double.IsFinite(presenter.Width) ? presenter.Width : Math.Max(1, availableSize.Width);
        presenter.Measure(new Size(width, double.PositiveInfinity));
        return new Size(width, 1);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_presenter is { } presenter)
        {
            var width = double.IsFinite(presenter.Width) ? presenter.Width : Math.Max(1, finalSize.Width);
            presenter.Arrange(new Rect(0, 0, width, Math.Max(1, presenter.DesiredSize.Height)));
        }
        return finalSize;
    }

    private bool IsCurrent(long generation, ContentPresenter presenter)
        => !_disposed
           && generation == _generation
           && ReferenceEquals(_presenter, presenter)
           && !(_preparationCancellation?.IsCancellationRequested ?? true);

    private void DestroyOwnedPresenter()
    {
        if (_presenter is not { } presenter)
        {
            return;
        }
        _presenter = null;
        foreach (var markdown in presenter.GetVisualDescendants().OfType<StreamingMarkdownPresenter>())
        {
            markdown.MarkdownBuilder = null;
        }
        Children.Remove(presenter);
        presenter.Content = null;
        presenter.ContentTemplate = null;
        TranscriptToolDiagnostics.DetailVisualDestroyed();
    }

    private static long Quantize(double value)
        => double.IsFinite(value) ? checked((long)Math.Round(value * 4)) : 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CancelPreparation();
    }

    private readonly record struct PreparationFingerprint(long Width, long Height, long GeometryRevision);
}

internal sealed record PreparedTranscriptToolVisual(
    TranscriptToolPreparationPortal Portal,
    long Generation,
    object Content,
    ContentPresenter Presenter,
    double Height);
