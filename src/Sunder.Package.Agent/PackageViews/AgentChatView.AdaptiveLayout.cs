using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentChatView
{
    private void ApplyHeaderLayout()
    {
        var useWideLayout = Bounds.Width >= WideHeaderMinimumWidth;
        if (!_headerLayoutInitialized || useWideLayout != _usesWideLayout)
        {
            if (_headerLayoutInitialized)
            {
                CaptureComposerEditorState(_usesWideLayout, _lastComposerExpanded);
            }

            _headerLayoutInitialized = true;
            _usesWideLayout = useWideLayout;
            HeaderWideLayout.IsVisible = useWideLayout;
            HeaderNarrowLayout.IsVisible = !useWideLayout;
            TranscriptScrollContent.Classes.Set("compact", !useWideLayout);
            QueueRestoreComposerEditorState();
        }

        QueueWorkspacePathChipLayoutUpdate();
    }

    private void OnWorkspacePathRowSizeChanged(object? sender, SizeChangedEventArgs e)
        => QueueWorkspacePathChipLayoutUpdate();

    private void QueueWorkspacePathChipLayoutUpdate()
    {
        lock (_workspacePathLayoutSyncRoot)
        {
            _workspacePathLayoutDirty = true;
            if (_workspacePathLayoutQueued)
            {
                return;
            }
            _workspacePathLayoutQueued = true;
        }

        _tasks.Run(async cancellationToken =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    lock (_workspacePathLayoutSyncRoot)
                    {
                        _workspacePathLayoutDirty = false;
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            UpdateWorkspacePathChipLayout();
                        }
                    }, DispatcherPriority.Loaded);

                    lock (_workspacePathLayoutSyncRoot)
                    {
                        if (_workspacePathLayoutDirty)
                        {
                            continue;
                        }

                        _workspacePathLayoutQueued = false;
                        return;
                    }
                }
            }
            finally
            {
                lock (_workspacePathLayoutSyncRoot)
                {
                    _workspacePathLayoutQueued = false;
                }
            }
        });
    }

    private void CaptureComposerEditorState(bool wide, bool expanded)
    {
        var textBox = expanded ? ExpandedComposerTextBox : CollapsedComposerTextBox;
        _editorStateCache.Save(wide, expanded, new AgentChatEditorState(
            textBox.CaretIndex,
            textBox.SelectionStart,
            textBox.SelectionEnd,
            textBox.IsKeyboardFocusWithin));
    }

    private void QueueRestoreComposerEditorState()
        => _tasks.Run(async cancellationToken =>
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested
                    || !_editorStateCache.TryRestore(_usesWideLayout, _lastComposerExpanded, out var state))
                {
                    return;
                }

                var textBox = _lastComposerExpanded ? ExpandedComposerTextBox : CollapsedComposerTextBox;
                var textLength = textBox.Text?.Length ?? 0;
                textBox.CaretIndex = Math.Clamp(state.CaretIndex, 0, textLength);
                textBox.SelectionStart = Math.Clamp(state.SelectionStart, 0, textLength);
                textBox.SelectionEnd = Math.Clamp(state.SelectionEnd, 0, textLength);
                if (state.HadFocus)
                {
                    textBox.Focus();
                }
            }, DispatcherPriority.Loaded);
        });

    private void UpdateWorkspacePathChipLayout()
    {
        var viewModel = _viewModel ?? DataContext as AgentChatViewModel;
        if (viewModel is null)
        {
            return;
        }

        var labels = viewModel.WorkspacePathChipLabels;
        var wideVisibleCount = CalculateVisibleWorkspacePathChipCount(labels, WideWorkspacePathRow.Bounds.Width);
        var narrowVisibleCount = CalculateVisibleWorkspacePathChipCount(labels, NarrowWorkspacePathRow.Bounds.Width);
        viewModel.UpdateWorkspacePathChipLayout(wideVisibleCount, narrowVisibleCount);
    }

    private int CalculateVisibleWorkspacePathChipCount(IReadOnlyList<string> labels, double availableWidth)
    {
        if (labels.Count == 0)
        {
            return 0;
        }

        if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
        {
            return labels.Count;
        }

        var prefixWidths = new double[labels.Count + 1];
        for (var index = 0; index < labels.Count; index++)
        {
            prefixWidths[index + 1] = prefixWidths[index]
                + (index > 0 ? WorkspacePathChipSpacing : 0)
                + MeasureWorkspacePathTextWidth(labels[index])
                + WorkspacePathChipChromeWidth;
        }

        for (var count = labels.Count; count >= 0; count--)
        {
            var overflowCount = labels.Count - count;
            var width = prefixWidths[count];
            if (overflowCount > 0)
            {
                width += WorkspacePathOverflowSpacing + MeasureWorkspacePathOverflowWidth(overflowCount);
            }

            if (width <= availableWidth)
            {
                return count;
            }
        }

        return 0;
    }

    private double MeasureWorkspacePathOverflowWidth(int overflowCount)
        => MeasureWorkspacePathTextWidth($"+{overflowCount} more") + WorkspacePathOverflowChromeWidth;

    private double MeasureWorkspacePathTextWidth(string text)
    {
        if (_workspacePathTextWidths.TryGetValue(text, out var cachedWidth))
        {
            return cachedWidth;
        }

        var textBlock = new TextBlock
        {
            Text = text,
            FontFamily = WorkspacePathChipFontFamily,
            FontSize = WorkspacePathChipTextFontSize,
        };
        textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Ceiling(textBlock.DesiredSize.Width);
        _workspacePathTextWidths[text] = width;
        return width;
    }
}
