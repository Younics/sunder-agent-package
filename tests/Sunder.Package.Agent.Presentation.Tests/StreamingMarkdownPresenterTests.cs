using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
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

        Assert.True(renderedStructuredContent);
        Assert.Contains(descendants.OfType<Border>(), border => border.Classes.Contains("Heading2Block"));
        Assert.Contains(descendants, visual => visual is MarkdownListGrid);
        Assert.Contains(descendants, visual => visual is MarkdownTableGrid);
        Assert.Contains(descendants, visual => visual is CodeBlock);
        Assert.DoesNotContain(descendants.OfType<SelectableTextBlock>(), block => block.Classes.Contains("markdown-fallback"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task PendingRenderer_DoesNotChangeLayoutWhileSelectionDefersPromotion()
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
            presenter.GetVisualDescendants().OfType<MarkdownRenderer>());
        currentRenderer.SelectAll();
        Assert.True(currentRenderer.CanCopy);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var initialHeight = presenter.Bounds.Height;

        builder.Append("\n\n" + string.Join(
            "\n\n",
            Enumerable.Repeat(
                "## Expanded heading\n\nA paragraph that wraps across the available transcript width.",
                40)));
        await WaitUntilAsync(window, () =>
        {
            var renderers = presenter.GetVisualDescendants()
                .OfType<MarkdownRenderer>()
                .ToArray();
            return renderers.Length == 2
                   && renderers.Any(renderer => renderer.Opacity == 0
                                                && renderer.GetVisualDescendants().Skip(1).Any());
        });
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        Assert.Equal(1, renderedCount);
        Assert.InRange(Math.Abs(presenter.Bounds.Height - initialHeight), 0, 1);

        currentRenderer.ClearSelection();
        await WaitUntilAsync(window, () => renderedCount == 2);
        Assert.Single(presenter.GetVisualDescendants().OfType<MarkdownRenderer>());
        Assert.True(presenter.Bounds.Height > initialHeight + 20);
        window.Close();
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
            presenter.GetVisualDescendants().OfType<MarkdownRenderer>());

        renderer.Height = renderer.Bounds.Height + 80;
        await WaitUntilAsync(window, () => renderedCount >= 2);

        window.Close();
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
            presenter.GetVisualDescendants().OfType<MarkdownRenderer>());
        Assert.Equal("## Old\n\n- item", currentRenderer.MarkdownBuilder?.ToString());

        presenter.MarkdownBuilder = new ObservableStringBuilder("## New row\n\nFinal content.");
        await WaitUntilAsync(window, () => renderedCount >= 3);
        var replacementRenderer = Assert.Single(
            presenter.GetVisualDescendants().OfType<MarkdownRenderer>());

        Assert.NotSame(currentRenderer, replacementRenderer);
        Assert.Equal("## New row\n\nFinal content.", replacementRenderer.MarkdownBuilder?.ToString());
        Assert.DoesNotContain(
            presenter.GetVisualDescendants().OfType<MarkdownRenderer>(),
            renderer => renderer.MarkdownBuilder?.ToString().Contains("Old", StringComparison.Ordinal) == true);
        window.Close();
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

        Assert.Single(root.GetVisualDescendants().OfType<MarkdownRenderer>());
        Assert.Empty(hiddenPresenter.GetVisualDescendants().OfType<MarkdownRenderer>());
        window.Close();
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
            () => presenter.GetVisualDescendants().OfType<MarkdownRenderer>().Any());

        host.IsVisible = false;
        builder.Append(" updated while hidden");
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.Empty(presenter.GetVisualDescendants().OfType<MarkdownRenderer>());

        host.IsVisible = true;
        await WaitUntilAsync(
            window,
            () => presenter.GetVisualDescendants()
                .OfType<MarkdownRenderer>()
                .Any(renderer => renderer.MarkdownBuilder?.ToString()
                    == "Initial content updated while hidden"));

        var renderer = Assert.Single(
            presenter.GetVisualDescendants().OfType<MarkdownRenderer>());
        Assert.Equal(
            "Initial content updated while hidden",
            renderer.MarkdownBuilder?.ToString());
        window.Close();
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
}
