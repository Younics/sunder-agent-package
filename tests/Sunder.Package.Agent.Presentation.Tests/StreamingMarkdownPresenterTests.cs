using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Markdig;
using Sunder.Package.Agent.Shared.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class StreamingMarkdownPresenterTests
{
    [AvaloniaFact]
    public async Task Presenter_RendersHeadingListTableAndCodeBlock()
    {
        var builder = new ObservableStringBuilder(
            "## Heading\n\n- first\n- second\n\n| Name | Value |\n| --- | --- |\n| A | B |\n\n```csharp\nvar value = 1;\n```\n");
        var presenter = new StreamingMarkdownPresenter { MarkdownBuilder = builder };
        var renderedCount = 0;
        var renderedStructuredContent = false;
        presenter.Rendered += (_, _) =>
        {
            renderedCount++;
            var descendants = presenter.GetVisualDescendants().ToArray();
            renderedStructuredContent = descendants.OfType<Border>()
                                            .Any(border => border.Classes.Contains("Heading2Block"))
                                        && descendants.Any(visual => visual is MarkdownListGrid)
                                        && descendants.Any(visual => visual is MarkdownTableGrid)
                                        && descendants.Any(visual => visual is CodeBlock);
        };
        var window = new Window { Width = 700, Height = 600, Content = presenter };
        window.Show();

        await WaitUntilAsync(window, () => renderedCount > 0);
        var descendants = presenter.GetVisualDescendants().ToArray();

        Assert.False(presenter.IsRenderPending);
        Assert.True(renderedStructuredContent);
        Assert.Contains(descendants.OfType<Border>(), border => border.Classes.Contains("Heading2Block"));
        Assert.Contains(descendants, visual => visual is MarkdownListGrid);
        Assert.Contains(descendants, visual => visual is MarkdownTableGrid);
        Assert.Contains(descendants, visual => visual is CodeBlock);
        Assert.DoesNotContain(descendants.OfType<SelectableTextBlock>(), block => block.Classes.Contains("markdown-fallback"));
        await presenter.PendingRenderOperations;
        await CloseWindowAsync(window);
    }

    [AvaloniaFact]
    public async Task SelectionDefersIncrementalUpdateWithoutReplacingRenderer()
    {
        var builder = new ObservableStringBuilder("Initial response.");
        var presenter = new StreamingMarkdownPresenter { MarkdownBuilder = builder };
        var renderedCount = 0;
        presenter.Rendered += (_, _) => renderedCount++;
        var root = new StackPanel { Children = { presenter } };
        var window = new Window { Width = 500, Height = 800, Content = root };
        window.Show();
        await WaitUntilAsync(window, () => renderedCount == 1);
        var currentRenderer = Assert.Single(
            presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>());
        currentRenderer.SelectAll();
        Assert.True(currentRenderer.CanCopy);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var initialHeight = presenter.Bounds.Height;

        builder.Append("\n\n" + string.Join(
            "\n\n",
            Enumerable.Repeat(
                "## Expanded heading\n\nA paragraph that wraps across the available transcript width.",
                40)));
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        Assert.Equal(1, renderedCount);
        Assert.InRange(Math.Abs(presenter.Bounds.Height - initialHeight), 0, 1);
        Assert.Same(currentRenderer, Assert.Single(
            presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>()));
        Assert.Equal("Initial response.", currentRenderer.SourceBuilder?.ToString());
        Assert.Equal(1, presenter.RendererCreationCount);

        currentRenderer.ClearSelection();
        var renderedBeforeSelectionRelease = renderedCount;
        await WaitUntilAsync(window, () =>
            currentRenderer.SourceBuilder?.ToString().Contains("Expanded heading", StringComparison.Ordinal) == true
            && presenter.Bounds.Height > initialHeight + 20
            && renderedCount > renderedBeforeSelectionRelease);
        Assert.Same(currentRenderer, Assert.Single(
            presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>()));
        Assert.Equal(1, presenter.RendererCreationCount);
        Assert.True(presenter.Bounds.Height > initialHeight + 20);
        await presenter.PendingRenderOperations;
        await CloseWindowAsync(window);
    }

    [AvaloniaFact]
    public async Task SelectionStartedDuringParseDefersDocumentApplication()
    {
        var builder = new ObservableStringBuilder("Initial response.");
        var presenter = new StreamingMarkdownPresenter { MarkdownBuilder = builder };
        var renderedCount = 0;
        presenter.Rendered += (_, _) => renderedCount++;
        var window = new Window { Width = 500, Height = 500, Content = presenter };
        window.Show();
        await WaitUntilAsync(window, () => renderedCount == 1);
        var renderer = Assert.Single(
            presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>());
        var expandedSource = "Initial response.\n\n" + string.Join(
            "\n\n",
            Enumerable.Range(0, 5000).Select(index => $"## Expanded heading {index}"));

        builder.Append(expandedSource["Initial response.".Length..]);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        renderer.SelectAll();
        await renderer.PendingRenderOperations;

        Assert.Equal("Initial response.", renderer.RenderedSource);
        Assert.True(renderer.CanCopy);
        Assert.True(presenter.IsRenderPending);

        renderer.ClearSelection();
        await WaitUntilAsync(window, () =>
            renderer.RenderedSource == expandedSource
            && !presenter.IsRenderPending);

        Assert.Equal(1, presenter.RendererCreationCount);
        Assert.False(presenter.IsRenderPending);
        await presenter.PendingRenderOperations;
        await CloseWindowAsync(window);
    }

    [AvaloniaFact]
    public async Task CurrentRenderer_LateLayoutChangeRaisesRendered()
    {
        var presenter = new StreamingMarkdownPresenter
        {
            MarkdownBuilder = new ObservableStringBuilder("Initial response."),
        };
        var renderedCount = 0;
        presenter.Rendered += (_, _) => renderedCount++;
        var root = new StackPanel { Children = { presenter } };
        var window = new Window { Width = 500, Height = 500, Content = root };
        window.Show();
        await WaitUntilAsync(window, () => renderedCount >= 1);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var renderer = Assert.Single(
            presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>());

        var renderedBeforeHeightChange = renderedCount;
        renderer.Height = renderer.Bounds.Height + 80;
        await WaitUntilAsync(window, () => renderedCount > renderedBeforeHeightChange);

        await presenter.PendingRenderOperations;
        await CloseWindowAsync(window);
    }

    [AvaloniaFact]
    public async Task UnchangedHeightSourceRaisesRenderedSettlement()
    {
        var builder = new ObservableStringBuilder("Alpha");
        var presenter = new StreamingMarkdownPresenter { MarkdownBuilder = builder };
        var renderedCount = 0;
        presenter.Rendered += (_, _) => renderedCount++;
        var window = new Window { Width = 500, Height = 300, Content = presenter };
        window.Show();
        await WaitUntilAsync(window, () => renderedCount == 1);
        var initialHeight = presenter.Bounds.Height;

        builder.Clear();
        builder.Append("Bravo");
        await WaitUntilAsync(window, () => renderedCount == 2);

        Assert.InRange(Math.Abs(presenter.Bounds.Height - initialHeight), 0, 1);
        Assert.False(presenter.IsRenderPending);
        await presenter.PendingRenderOperations;
        await CloseWindowAsync(window);
    }

    [AvaloniaFact]
    public async Task InitialFallbackPromotesAfterSelectionIsReleased()
    {
        var source = string.Join(
            "\n\n",
            Enumerable.Range(0, 5000).Select(index => $"Paragraph {index}: {new string('x', 80)}"));
        var presenter = new StreamingMarkdownPresenter
        {
            MarkdownBuilder = new ObservableStringBuilder(source),
        };
        var renderedCount = 0;
        presenter.Rendered += (_, _) => renderedCount++;
        var window = new Window { Width = 500, Height = 500, Content = presenter };
        window.Show();
        await WaitUntilAsync(window, () => presenter.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .Any(block => block.Classes.Contains("markdown-fallback")));
        var fallback = presenter.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .Single(block => block.Classes.Contains("markdown-fallback"));
        fallback.SelectAll();

        await WaitUntilAsync(window, () => presenter.GetVisualDescendants()
            .OfType<StableMarkdownRenderer>()
            .Any(renderer => renderer.RenderedSource == source));
        Assert.Contains(fallback, presenter.GetVisualDescendants());
        Assert.Equal(0, renderedCount);

        fallback.ClearSelection();
        await WaitUntilAsync(window, () => renderedCount == 1);

        Assert.DoesNotContain(fallback, presenter.GetVisualDescendants());
        await presenter.PendingRenderOperations;
        await CloseWindowAsync(window);
    }

    [AvaloniaFact]
    public async Task OverlappingParsesApplyOnlyLatestSourceToPersistentRenderer()
    {
        var builder = new ObservableStringBuilder("Initial response.");
        var presenter = new StreamingMarkdownPresenter { MarkdownBuilder = builder };
        var renderedCount = 0;
        presenter.Rendered += (_, _) => renderedCount++;
        var window = new Window { Width = 500, Height = 500, Content = presenter };
        window.Show();
        await WaitUntilAsync(window, () => renderedCount == 1);
        var renderer = Assert.Single(
            presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>());

        builder.Append("\n\n" + string.Join(
            "\n\n",
            Enumerable.Range(0, 5000).Select(index => $"## Stale heading {index}")));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        builder.Clear();
        builder.Append("## Final heading\n\nFinal marker.");

        await WaitUntilAsync(window, () =>
            renderer.RenderedSource == "## Final heading\n\nFinal marker."
            && renderer.GetVisualDescendants()
                .OfType<MarkdownTextBlock>()
                .Any(block => block.ActualText.Contains("Final marker.", StringComparison.Ordinal)));

        Assert.Same(renderer, Assert.Single(
            presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>()));
        Assert.Equal(1, presenter.RendererCreationCount);
        Assert.DoesNotContain(
            renderer.GetVisualDescendants().OfType<MarkdownTextBlock>(),
            block => block.ActualText.Contains("Stale heading", StringComparison.Ordinal));
        await presenter.PendingRenderOperations;
        await CloseWindowAsync(window);
    }

    [AvaloniaFact]
    public async Task FailedStaleParseContinuesWithLatestSource()
    {
        var parseStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var parseAttempt = 0;
        var builder = new ObservableStringBuilder("stale source");
        var renderer = StableMarkdownRenderer.Create(source =>
        {
            if (Interlocked.Increment(ref parseAttempt) == 1)
            {
                parseStarted.TrySetResult();
                releaseParse.Task.GetAwaiter().GetResult();
                throw new InvalidOperationException("Injected stale parse failure.");
            }
            return Markdown.Parse(source, pipeline);
        });
        renderer.SourceBuilder = builder;
        var window = new Window { Width = 500, Height = 300, Content = renderer };
        window.Show();
        await parseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        builder.Clear();
        builder.Append("## Latest source\n\nFinal marker.");
        releaseParse.TrySetResult();

        await WaitUntilAsync(
            window,
            () => renderer.RenderedSource == "## Latest source\n\nFinal marker.");

        Assert.True(parseAttempt >= 2);
        await renderer.PendingRenderOperations;
        await CloseWindowAsync(window);
    }

    [AvaloniaFact]
    public async Task FailedCurrentParseRetainsFullDirtyRangeForNextUpdate()
    {
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var parseAttempt = 0;
        var builder = new ObservableStringBuilder("## Initial heading\n\nInitial marker.");
        var renderer = StableMarkdownRenderer.Create(source =>
        {
            if (Interlocked.Increment(ref parseAttempt) == 1)
            {
                throw new InvalidOperationException("Injected current parse failure.");
            }

            return Markdown.Parse(source, pipeline);
        });
        renderer.SourceBuilder = builder;
        var window = new Window { Width = 500, Height = 300, Content = renderer };
        window.Show();
        await renderer.PendingRenderOperations;
        Assert.True(renderer.HasTerminalRenderFailure);

        builder.Append("\n\nFinal marker.");
        await WaitUntilAsync(window, () =>
            renderer.RenderedSource.EndsWith("Final marker.", StringComparison.Ordinal)
            && renderer.GetVisualDescendants()
                .OfType<MarkdownTextBlock>()
                .Any(block => block.ActualText.Contains("Initial marker.", StringComparison.Ordinal))
            && renderer.GetVisualDescendants()
                .OfType<MarkdownTextBlock>()
                .Any(block => block.ActualText.Contains("Final marker.", StringComparison.Ordinal)));

        Assert.Equal(2, parseAttempt);
        Assert.False(renderer.HasTerminalRenderFailure);
        await renderer.PendingRenderOperations;
        await CloseWindowAsync(window);
    }

    [AvaloniaFact]
    public async Task ReplacingSourceDuringParseCancelsApplicationAndRendersLatestBuilder()
    {
        var parseStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var renderer = StableMarkdownRenderer.Create(source =>
        {
            if (source == "stale source")
            {
                parseStarted.TrySetResult();
                releaseParse.Task.GetAwaiter().GetResult();
            }
            return Markdown.Parse(source, pipeline);
        });
        renderer.SourceBuilder = new ObservableStringBuilder("stale source");
        var window = new Window { Width = 500, Height = 300, Content = renderer };
        window.Show();
        await parseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        renderer.SourceBuilder = new ObservableStringBuilder("latest source");
        releaseParse.TrySetResult();

        await WaitUntilAsync(window, () => renderer.RenderedSource == "latest source");
        Assert.DoesNotContain(
            renderer.GetVisualDescendants().OfType<MarkdownTextBlock>(),
            block => block.ActualText.Contains("stale source", StringComparison.Ordinal));
        await renderer.PendingRenderOperations;
        await CloseWindowAsync(window);
    }

    [AvaloniaFact]
    public async Task Presenter_CoalescesRapidUpdatesAndIsolatesBuilderReplacement()
    {
        var builder = new ObservableStringBuilder("## Old");
        var presenter = new StreamingMarkdownPresenter { MarkdownBuilder = builder };
        var renderedCount = 0;
        presenter.Rendered += (_, _) => renderedCount++;
        var window = new Window { Width = 500, Height = 300, Content = presenter };
        window.Show();
        await WaitUntilAsync(window, () => renderedCount >= 1);

        builder.Append("\n\n-");
        builder.Append(" ");
        builder.Append("item");
        await WaitUntilAsync(window, () => renderedCount >= 2);
        var currentRenderer = Assert.Single(
            presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>());
        Assert.Equal("## Old\n\n- item", currentRenderer.SourceBuilder?.ToString());
        Assert.Equal(1, presenter.RendererCreationCount);

        presenter.MarkdownBuilder = new ObservableStringBuilder("## New row\n\nFinal content.");
        await WaitUntilAsync(window, () => renderedCount >= 3);
        var replacementRenderer = Assert.Single(
            presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>());

        Assert.NotSame(currentRenderer, replacementRenderer);
        Assert.Equal("## New row\n\nFinal content.", replacementRenderer.SourceBuilder?.ToString());
        Assert.DoesNotContain(
            presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>(),
            renderer => renderer.SourceBuilder?.ToString().Contains("Old", StringComparison.Ordinal) == true);
        await presenter.PendingRenderOperations;
        await CloseWindowAsync(window);
    }

    [AvaloniaFact]
    public async Task HiddenRoleBranch_DoesNotCreateSecondRenderer()
    {
        var builder = new ObservableStringBuilder("Rendered once.");
        var visiblePresenter = new StreamingMarkdownPresenter { MarkdownBuilder = builder };
        var visibleRenderedCount = 0;
        visiblePresenter.Rendered += (_, _) => visibleRenderedCount++;
        var hiddenPresenter = new StreamingMarkdownPresenter
        {
            MarkdownBuilder = builder,
            IsVisible = false,
        };
        var root = new Grid
        {
            Children =
            {
                visiblePresenter,
                hiddenPresenter,
            },
        };
        var window = new Window { Width = 500, Height = 300, Content = root };
        window.Show();

        await WaitUntilAsync(
            window,
            () => visibleRenderedCount > 0);

        Assert.Single(root.GetVisualDescendants().OfType<StableMarkdownRenderer>());
        Assert.Empty(hiddenPresenter.GetVisualDescendants().OfType<StableMarkdownRenderer>());
        await Task.WhenAll(
            visiblePresenter.PendingRenderOperations,
            hiddenPresenter.PendingRenderOperations);
        await CloseWindowAsync(window);
    }

    [AvaloniaFact]
    public async Task AncestorVisibility_RendersLatestContentWhenShownAgain()
    {
        var builder = new ObservableStringBuilder("Initial content");
        var presenter = new StreamingMarkdownPresenter { MarkdownBuilder = builder };
        var host = new Border { Child = presenter };
        var window = new Window { Width = 500, Height = 300, Content = host };
        window.Show();
        await WaitUntilAsync(
            window,
            () => presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>().Any());

        host.IsVisible = false;
        builder.Append(" updated while hidden");
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var retainedRenderer = Assert.Single(
            presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>());
        Assert.Equal("Initial content", retainedRenderer.SourceBuilder?.ToString());
        Assert.Equal(1, presenter.RendererCreationCount);

        host.IsVisible = true;
        await WaitUntilAsync(
            window,
            () => presenter.GetVisualDescendants()
                .OfType<StableMarkdownRenderer>()
                .Any(renderer => renderer.RenderedSource
                    == "Initial content updated while hidden"));

        var renderer = Assert.Single(
            presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>());
        Assert.Same(retainedRenderer, renderer);
        Assert.Equal(
            "Initial content updated while hidden",
            renderer.RenderedSource);
        Assert.Equal(1, presenter.RendererCreationCount);
        await presenter.PendingRenderOperations;
        await CloseWindowAsync(window);
    }

    [AvaloniaFact]
    public async Task RevisionGate_TracksFallbackParsePromotionAndLateHeightWithoutDelay()
    {
        var parseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var builder = new ObservableStringBuilder(
            "## Gated content\n\n" + string.Join("\n\n", Enumerable.Repeat("A wrapping paragraph.", 30)));
        var presenter = new StreamingMarkdownPresenter(() => StableMarkdownRenderer.Create(source =>
        {
            if (source.Contains("Gated content", StringComparison.Ordinal))
            {
                parseStarted.TrySetResult();
                releaseParse.Task.GetAwaiter().GetResult();
            }
            return Markdown.Parse(source, pipeline);
        }))
        {
            MarkdownBuilder = builder,
        };
        var renderedCount = 0;
        var geometrySignals = 0;
        presenter.Rendered += (_, _) => renderedCount++;
        presenter.GeometryChanged += (_, _) => geometrySignals++;
        var window = new Window { Width = 420, Height = 500, Content = presenter };
        window.Show();

        await parseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await PumpLayoutUntilAsync(window, () => presenter.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .Any(block => block.Classes.Contains("markdown-fallback")));

        Assert.True(presenter.IsGeometryPending);
        Assert.True(presenter.RequestedRevision > presenter.SettledRevision);
        Assert.Equal(0, renderedCount);

        var pendingRenderer = Assert.Single(
            presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>());
        releaseParse.TrySetResult();
        await pendingRenderer.PendingRenderOperations.WaitAsync(TimeSpan.FromSeconds(2));
        await PumpLayoutUntilAsync(window, () =>
            !presenter.IsGeometryPending
            && presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>()
                .Any(renderer => renderer.Opacity == 1));
        var renderer = Assert.Single(
            presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>());
        var settledRevision = presenter.SettledRevision;
        var geometryRevision = presenter.GeometryRevision;
        var renderedBeforeLateHeight = renderedCount;

        renderer.Height = renderer.Bounds.Height + 60;
        await PumpLayoutUntilAsync(window, () =>
            presenter.GeometryRevision > geometryRevision
            && renderedCount > renderedBeforeLateHeight);

        Assert.Equal(presenter.RequestedRevision, settledRevision);
        Assert.Equal(presenter.RequestedRevision, presenter.SettledRevision);
        Assert.True(geometrySignals >= 3);
        await presenter.PendingRenderOperations;
        await CloseWindowAsync(window);
    }

    [AvaloniaFact]
    public async Task ToolDetailsPortal_PreparesOneVisualAndCollapseDestroysRenderer()
    {
        var portal = new TranscriptToolPreparationPortal { Width = 480, Height = 1 };
        var host = new TranscriptToolDetailHost();
        var root = new Grid { Children = { portal, host } };
        var window = new Window { Width = 520, Height = 320, Content = root };
        window.Show();
        try
        {
            var template = new FuncDataTemplate<object>((_, _) => new Border
            {
                Padding = new Thickness(16),
                Child = new StreamingMarkdownPresenter
                {
                    MarkdownBuilder = new ObservableStringBuilder(
                        "## Staged details\n\n"
                        + string.Join("\n\n", Enumerable.Repeat("A wrapping staged paragraph.", 24))),
                },
            });
            var content = new object();
            var prepared = await portal.PrepareAsync(content, template, 480)
                .WaitAsync(TimeSpan.FromSeconds(2));
            var markdown = Assert.Single(
                prepared.Presenter.GetVisualDescendants().OfType<StreamingMarkdownPresenter>());
            var renderer = Assert.Single(
                markdown.GetVisualDescendants().OfType<StableMarkdownRenderer>());
            var owner = new TestExpansionOwner();
            Assert.True(portal.Commit(prepared, host, owner));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.True(host.Bounds.Height > 0);
            Assert.Equal(1, prepared.Presenter.Opacity);
            Assert.True(prepared.Presenter.IsHitTestVisible);
            Assert.True(prepared.Presenter.IsEffectivelyVisible);
            Assert.Equal(1, markdown.RendererCreationCount);

            owner.Invalidate();
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.False(host.HasDetailVisual);
            Assert.Empty(host.GetVisualDescendants().OfType<StableMarkdownRenderer>());

            var replacement = await portal.PrepareAsync(content, template, 300)
                .WaitAsync(TimeSpan.FromSeconds(2));
            var replacementRenderer = Assert.Single(
                replacement.Presenter.GetVisualDescendants().OfType<StableMarkdownRenderer>());
            Assert.NotSame(renderer, replacementRenderer);
        }
        finally
        {
            portal.Dispose();
            await CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ToolDetailsPortal_SupersededPreparationRequiresReplacementGeometry()
    {
        var first = new ControlledGeometrySource();
        first.Request();
        var portal = new TranscriptToolPreparationPortal { Width = 420, Height = 1 };
        var template = new FuncDataTemplate<object>((value, _) => (Control)value);
        var window = new Window { Width = 420, Height = 320, Content = portal };
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var preparation = portal.PrepareAsync(first, template, 420);
            Assert.False(preparation.IsCompleted);

            var replacement = new ControlledGeometrySource();
            replacement.Request();
            var replacementPreparation = portal.PrepareAsync(replacement, template, 420);
            first.Settle();
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await preparation);
            Assert.False(replacementPreparation.IsCompleted);
            replacement.Settle();
            var prepared = await replacementPreparation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Same(replacement, prepared.Presenter.Child);
        }
        finally
        {
            portal.Dispose();
            await CloseWindowAsync(window);
        }
    }

    private static async Task WaitUntilAsync(
        Window window,
        Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The Markdown presenter did not settle before the timeout.");
    }

    private static async Task CloseWindowAsync(Window window)
    {
        window.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.SystemIdle);
    }

    private static async Task PumpLayoutUntilAsync(Window window, Func<bool> condition)
    {
        for (var pass = 0; pass < 80; pass++)
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (condition())
            {
                return;
            }
        }

        var presenter = window.GetVisualDescendants()
            .OfType<StreamingMarkdownPresenter>()
            .SingleOrDefault();
        var renderer = presenter?.GetVisualDescendants()
            .OfType<StableMarkdownRenderer>()
            .SingleOrDefault();
        Assert.Fail(
            $"The controlled Markdown geometry stage did not settle. "
            + $"requested={presenter?.RequestedRevision}, settled={presenter?.SettledRevision}, "
            + $"geometry={presenter?.GeometryRevision}, pending={presenter?.IsGeometryPending}, "
            + $"renderPending={presenter?.IsRenderPending}, opacity={renderer?.Opacity}, "
            + $"renderedLength={renderer?.RenderedSource.Length}, "
            + $"presenterDesired={presenter?.DesiredSize}, rendererDesired={renderer?.DesiredSize}.");
    }

    private sealed class ControlledGeometrySource : Border, ITranscriptGeometrySource
    {
        public long RequestedRevision { get; private set; }

        public long SettledRevision { get; private set; }

        public long GeometryRevision { get; private set; }

        public bool IsGeometryPending => SettledRevision < RequestedRevision;

        public event EventHandler? GeometryChanged;

        public void Request()
        {
            RequestedRevision++;
            GeometryChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RequestSilently() => RequestedRevision++;

        public void Settle()
        {
            SettledRevision = RequestedRevision;
            GeometryRevision++;
            GeometryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class TestExpansionOwner : ITranscriptToolExpansionOwner
    {
        public event Action? DetailVisualInvalidated;

        public void OnDetailVisualInvalidated()
        {
        }

        public void Invalidate() => DetailVisualInvalidated?.Invoke();
    }
}
