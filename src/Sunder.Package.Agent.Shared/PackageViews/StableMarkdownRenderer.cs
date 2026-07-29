using Avalonia;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using LiveMarkdown.Avalonia;
using Markdig;
using Markdig.Syntax;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed class StableMarkdownRenderer : MarkdownRenderer
{
    private readonly DocumentNode _documentNode;
    private readonly MarkdownPipeline _pipeline;
    private readonly Func<string, MarkdownDocument> _parseMarkdown;
    private ObservableStringBuilder? _sourceBuilder;
    private ObservableStringBuilderChangedEventArgs? _pendingChange;
    private Task _renderOperation = Task.CompletedTask;
    private CancellationTokenSource? _renderCancellation;
    private DeferredRender? _deferredRender;
    private int _sourceGeneration;
    private bool _renderLoopRunning;

    internal static StableMarkdownRenderer Create(
        Func<string, MarkdownDocument>? parseMarkdown = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        MarkdownPipelineBuilder? pipelineBuilder = null;
        void CapturePipelineBuilder(MarkdownPipelineBuilder builder) => pipelineBuilder = builder;

        MarkdownRenderer.ConfigurePipeline += CapturePipelineBuilder;
        try
        {
            return new StableMarkdownRenderer(
                parseMarkdown,
                () => pipelineBuilder);
        }
        finally
        {
            MarkdownRenderer.ConfigurePipeline -= CapturePipelineBuilder;
        }
    }

    private StableMarkdownRenderer(
        Func<string, MarkdownDocument>? parseMarkdown,
        Func<MarkdownPipelineBuilder?> getPipelineBuilder)
    {
        foreach (var child in VisualChildren.ToArray())
        {
            if (child is ILogical logical)
            {
                LogicalChildren.Remove(logical);
            }
            VisualChildren.Remove(child);
        }

        _documentNode = new DocumentNode(this);
        var pipelineBuilder = getPipelineBuilder()
            ?? throw new InvalidOperationException("LiveMarkdown did not configure its Markdown pipeline.");
        _pipeline = pipelineBuilder.Build();
        _parseMarkdown = parseMarkdown ?? (source => Markdown.Parse(source, _pipeline));
        LogicalChildren.Add(_documentNode.Control);
        VisualChildren.Add(_documentNode.Control);
    }

    internal Task PendingRenderOperations => _renderOperation;

    internal string RenderedSource { get; private set; } = string.Empty;

    internal bool HasTerminalRenderFailure { get; private set; }

    internal event EventHandler? RenderStateChanged;

    public ObservableStringBuilder? SourceBuilder
    {
        get => _sourceBuilder;
        set
        {
            if (ReferenceEquals(_sourceBuilder, value))
            {
                return;
            }

            Dispatcher.UIThread.VerifyAccess();
            if (_sourceBuilder is not null)
            {
                _sourceBuilder.Changed -= OnSourceChanged;
            }
            _renderCancellation?.Cancel();
            _renderCancellation?.Dispose();
            _renderCancellation = value is null ? null : new CancellationTokenSource();

            _sourceBuilder = value;
            _sourceGeneration++;
            _pendingChange = null;
            _deferredRender = null;
            RenderedSource = string.Empty;
            HasTerminalRenderFailure = false;
            if (value is null)
            {
                return;
            }

            value.Changed += OnSourceChanged;
            _pendingChange = new ObservableStringBuilderChangedEventArgs(
                0,
                value.Length,
                value.Length,
                value.Version);
            EnsureRenderLoop();
        }
    }

    private void OnSourceChanged(in ObservableStringBuilderChangedEventArgs change)
    {
        Dispatcher.UIThread.VerifyAccess();
        _sourceGeneration++;
        _deferredRender = null;
        HasTerminalRenderFailure = false;
        _pendingChange = Merge(_pendingChange, change);
        EnsureRenderLoop();
    }

    private void EnsureRenderLoop()
    {
        if (_renderLoopRunning)
        {
            return;
        }

        _renderLoopRunning = true;
        var cancellationToken = _renderCancellation?.Token ?? CancellationToken.None;
        using (ExecutionContext.SuppressFlow())
        {
            _renderOperation = Task.Run(() => RenderLatestSourceAsync(cancellationToken));
        }
    }

    private async Task RenderLatestSourceAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                var request = await Dispatcher.UIThread.InvokeAsync(CaptureRenderRequest);
                if (request is null)
                {
                    return;
                }

                MarkdownDocument document;
                try
                {
                    TranscriptToolDiagnostics.MarkdownParsed();
                    document = _parseMarkdown(request.Source);
                }
                catch (Exception exception)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        await ReportRenderFailureAsync(exception, request.Generation).ConfigureAwait(false);
                    }
                    return;
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                bool completed;
                try
                {
                    completed = await Dispatcher.UIThread.InvokeAsync(
                        () => cancellationToken.IsCancellationRequested
                              || ApplyParsedDocument(request, document));
                }
                catch (Exception exception)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        await ReportRenderFailureAsync(exception, request.Generation).ConfigureAwait(false);
                    }
                    return;
                }
                if (completed)
                {
                    return;
                }
            }
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await CompleteCanceledRenderLoopAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task CompleteCanceledRenderLoopAsync()
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _renderLoopRunning = false;
                if (_sourceBuilder is not null && _pendingChange is not null)
                {
                    EnsureRenderLoop();
                }
            });
        }
        catch
        {
            _renderLoopRunning = false;
        }
    }

    private RenderRequest? CaptureRenderRequest()
    {
        if (_sourceBuilder is null || _pendingChange is not { } change)
        {
            _renderLoopRunning = false;
            return null;
        }

        return new RenderRequest(
            _sourceGeneration,
            _sourceBuilder.ToString(),
            change);
    }

    private bool ApplyParsedDocument(RenderRequest request, MarkdownDocument document)
    {
        if (_sourceBuilder is null || request.Generation != _sourceGeneration)
        {
            return false;
        }
        if (CanCopy || IsKeyboardFocusWithin)
        {
            _deferredRender = new DeferredRender(request, document);
            _renderLoopRunning = false;
            return true;
        }

        _documentNode.Update(
            _documentNode,
            document,
            request.Change,
            CancellationToken.None);
        TranscriptToolDiagnostics.MarkdownApplied();
        _pendingChange = null;
        _deferredRender = null;
        RenderedSource = request.Source;
        HasTerminalRenderFailure = false;
        _renderLoopRunning = false;
        InvalidateMeasure();
        RenderStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal void ResumeDeferredRender()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (CanCopy
            || IsKeyboardFocusWithin
            || _deferredRender is not { } deferred)
        {
            return;
        }

        try
        {
            ApplyParsedDocument(deferred.Request, deferred.Document);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Error while rendering markdown: {exception.Message}");
            RecoverFromRenderFailure(deferred.Request.Generation);
        }
    }

    private async Task ReportRenderFailureAsync(Exception exception, int failedGeneration)
    {
        await Console.Error.WriteAsync($"Error while rendering markdown: {exception.Message}")
            .ConfigureAwait(false);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => RecoverFromRenderFailure(failedGeneration));
        }
        catch
        {
            _renderLoopRunning = false;
        }
    }

    private void RecoverFromRenderFailure(int failedGeneration)
    {
        _renderLoopRunning = false;
        if (_sourceBuilder is null)
        {
            return;
        }
        if (failedGeneration == _sourceGeneration)
        {
            _deferredRender = null;
            HasTerminalRenderFailure = true;
            RenderStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        EnsureRenderLoop();
    }

    private static ObservableStringBuilderChangedEventArgs Merge(
        ObservableStringBuilderChangedEventArgs? current,
        in ObservableStringBuilderChangedEventArgs change)
    {
        if (current is not { } pending)
        {
            return change;
        }

        var startIndex = Math.Min(pending.StartIndex, change.StartIndex);
        var endIndex = Math.Max(
            pending.StartIndex + pending.Length,
            change.StartIndex + change.Length);
        return new ObservableStringBuilderChangedEventArgs(
            startIndex,
            endIndex - startIndex,
            change.NewLength,
            change.Version);
    }

    private sealed record RenderRequest(
        int Generation,
        string Source,
        ObservableStringBuilderChangedEventArgs Change);

    private sealed record DeferredRender(RenderRequest Request, MarkdownDocument Document);
}
