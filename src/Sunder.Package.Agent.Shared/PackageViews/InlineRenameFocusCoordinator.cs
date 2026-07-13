using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed class InlineRenameFocusCoordinator<TItem> : IDisposable where TItem : class
{
    private const int FocusRetryLimit = 12;

    private readonly Visual _searchRoot;
    private readonly Func<TItem, object> _keySelector;
    private readonly Func<TItem, bool> _isRenameActive;
    private readonly Func<Control?> _dropDownProvider;
    private readonly string _textBoxClass;
    private readonly PresentationTaskScope _tasks = new();
    private object? _pendingKey;
    private bool _disposed;

    public InlineRenameFocusCoordinator(
        Visual searchRoot,
        Func<TItem, object> keySelector,
        Func<TItem, bool> isRenameActive,
        Func<Control?> dropDownProvider,
        string textBoxClass)
    {
        _searchRoot = searchRoot;
        _keySelector = keySelector;
        _isRenameActive = isRenameActive;
        _dropDownProvider = dropDownProvider;
        _textBoxClass = textBoxClass;
    }

    public void Request(TItem item, bool reopenDropDown)
    {
        if (_disposed)
        {
            return;
        }

        _pendingKey = _keySelector(item);
        QueueFindAndFocus(item, reopenDropDown);
    }

    public void TryFocusAttached(TextBox? textBox)
    {
        if (_disposed
            || textBox?.DataContext is not TItem item
            || !_isRenameActive(item)
            || !Equals(_pendingKey, _keySelector(item))
            || !textBox.IsEffectivelyVisible)
        {
            return;
        }

        QueueFocus(textBox, item, 0);
    }

    public void Dispose()
    {
        _disposed = true;
        _pendingKey = null;
        _tasks.Dispose();
    }

    private void QueueFindAndFocus(TItem item, bool reopenDropDown, int attempt = 0)
    {
        Queue(() =>
        {
            if (!IsPending(item))
            {
                return;
            }

            if (reopenDropDown && _dropDownProvider() is ComboBox comboBox)
            {
                comboBox.IsDropDownOpen = true;
            }

            var textBox = FindTextBox(item);
            if (textBox is not null)
            {
                QueueFocus(textBox, item, attempt);
            }
            else if (attempt < FocusRetryLimit)
            {
                QueueFindAndFocus(item, reopenDropDown: false, attempt + 1);
            }
        }, DispatcherPriority.Background);
    }

    private void QueueFocus(TextBox textBox, TItem item, int attempt)
        => Queue(
            () => Focus(textBox, item, attempt),
            DispatcherPriority.ContextIdle);

    private void Focus(TextBox textBox, TItem item, int attempt)
    {
        if (!IsPending(item))
        {
            return;
        }

        if (textBox.IsEffectivelyVisible)
        {
            TopLevel.GetTopLevel(textBox)?.FocusManager?.Focus(
                textBox,
                NavigationMethod.Unspecified,
                KeyModifiers.None);
            textBox.Focus();
            var caretIndex = textBox.Text?.Length ?? 0;
            textBox.CaretIndex = caretIndex;
            textBox.SelectionStart = caretIndex;
            textBox.SelectionEnd = caretIndex;
        }

        Queue(() =>
        {
            if (!IsPending(item))
            {
                return;
            }

            if (textBox.IsKeyboardFocusWithin)
            {
                _pendingKey = null;
            }
            else if (attempt < FocusRetryLimit)
            {
                QueueFocus(textBox, item, attempt + 1);
            }
        }, DispatcherPriority.ContextIdle);
    }

    private void Queue(Action action, DispatcherPriority priority)
        => _tasks.Run(async cancellationToken =>
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    action();
                }
            }, priority);
        });

    private TextBox? FindTextBox(TItem item)
    {
        if (TopLevel.GetTopLevel(_searchRoot) is { } topLevel
            && FindTextBox(topLevel, item) is { } topLevelMatch)
        {
            return topLevelMatch;
        }

        return FindTextBox(_searchRoot, item);
    }

    private TextBox? FindTextBox(Visual root, TItem item)
        => root.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(textBox =>
                ReferenceEquals(textBox.DataContext, item)
                && textBox.Classes.Contains(_textBoxClass));

    private bool IsPending(TItem item)
        => !_disposed
           && _isRenameActive(item)
           && Equals(_pendingKey, _keySelector(item));
}
