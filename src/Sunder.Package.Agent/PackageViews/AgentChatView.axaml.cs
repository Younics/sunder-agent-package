using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentChatView : UserControl, IDisposable
{
    private const double WideHeaderMinimumWidth = 520;
    private const double WorkspacePathChipTextFontSize = 11;
    private const double WorkspacePathChipSpacing = 6;
    private const double WorkspacePathOverflowSpacing = 8;
    private const double WorkspacePathChipChromeWidth = 36;
    private const double WorkspacePathOverflowChromeWidth = 18;
    private static readonly FontFamily WorkspacePathChipFontFamily = new("Menlo,Consolas,monospace");
    private static readonly FilePickerFileType SupportedAttachmentFileType = new("Supported attachments")
    {
        Patterns =
        [
            "*.txt", "*.md", "*.json", "*.jsonl", "*.toml", "*.yaml", "*.yml", "*.xml", "*.csv", "*.log", "*.env",
            "*.cs", "*.js", "*.jsx", "*.ts", "*.tsx", "*.py", "*.go", "*.rs", "*.java", "*.c", "*.h", "*.cpp", "*.cc", "*.rb", "*.php", "*.swift", "*.sh", "*.sql", "*.html", "*.css", "*.diff", "*.patch",
            "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp", "*.svg", "*.pdf",
            "*.mp3", "*.m4a", "*.wav", "*.ogg", "*.opus", "*.aac",
            "*.mp4", "*.mov", "*.webm", "*.mpeg", "*.mpg", "*.avi"
        ],
    };

    private AgentChatViewModel? _viewModel;
    private readonly TranscriptViewBehavior _transcriptBehavior;
    private readonly InlineRenameFocusCoordinator<AgentSessionListItemViewModel> _renameFocus;
    private IPackageNotificationService _notificationService = NullPackageNotificationService.Instance;
    private bool _disposed;

    public AgentChatView()
    {
        InitializeComponent();
        ConfigureComposerDropTarget(ExpandedComposerDropTarget);
        ConfigureComposerDropTarget(ExpandedComposerTextBox);
        ConfigureComposerDropTarget(CollapsedComposerDropTarget);
        ConfigureComposerDropTarget(CollapsedComposerTextBox);
        ConfigureComposerKeyHandler(ExpandedComposerTextBox);
        ConfigureComposerKeyHandler(CollapsedComposerTextBox);
        _renameFocus = new InlineRenameFocusCoordinator<AgentSessionListItemViewModel>(
            this,
            session => session.SessionId,
            session => session.IsRenameActive,
            () => HeaderWideLayout.IsVisible ? WideSessionComboBox : NarrowSessionComboBox,
            "session-rename-input");
        _transcriptBehavior = new TranscriptViewBehavior(
            this,
            TranscriptScrollViewer,
            TranscriptItemsControl,
            JumpToLatestTranscriptButton,
            () => ViewModel?.CanLoadOlderTranscriptRows == true,
            anchor => ViewModel?.LoadOlderTranscriptRowsAsync(anchor) ?? Task.FromResult(false),
            () => ViewModel?.CanLoadNewerTranscriptRows == true,
            anchor => ViewModel?.LoadNewerTranscriptRowsAsync(anchor) ?? Task.FromResult(false),
            () => ViewModel?.HasNewerTranscriptRows == true,
            () => ViewModel?.IsTranscriptLoading == true,
            () => ViewModel?.Messages.Count > 0,
            () => ViewModel is { ShowSetupInstructions: false },
            () => ViewModel?.DetachTranscriptFromLatest(),
            () => ViewModel?.ResumeTranscriptFollowingLatestIfCaughtUp(),
            isVisible => ViewModel?.SetTranscriptJumpToLatestVisible(isVisible),
            anchor => ViewModel?.SetTranscriptViewportAnchor(anchor));
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    public AgentChatView(
        AgentProfileService profileService,
        AgentWorkspaceService workspaceService,
        AgentSessionService sessionService,
        AgentPermissionService permissionService,
        AgentRunCoordinator runCoordinator,
        AgentChatSelectionStateService selectionState,
        AgentToolPresentationService toolPresentationService,
        AgentExecutionTargetWarmupService warmupService,
        IPackageShellViewService shellViewService,
        AgentAttachmentService attachmentService,
        IPackageNotificationService notificationService)
        : this()
    {
        _notificationService = notificationService;
        _viewModel = new AgentChatViewModel(
            profileService,
            workspaceService,
            sessionService,
            permissionService,
            runCoordinator,
            selectionState,
            toolPresentationService,
            warmupService: warmupService,
            shellViewService: shellViewService,
            attachmentService: attachmentService);
        _viewModel.TranscriptChanging += OnTranscriptChanging;
        _viewModel.TranscriptChanged += OnTranscriptChanged;
        _viewModel.PropertyChanging += OnViewModelPropertyChanging;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = _viewModel;
    }

    private AgentChatViewModel? ViewModel => _viewModel ?? DataContext as AgentChatViewModel;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Loaded -= OnLoaded;
        SizeChanged -= OnSizeChanged;
        _transcriptBehavior.Dispose();
        _renameFocus.Dispose();
        if (_viewModel is not null)
        {
            _viewModel.TranscriptChanging -= OnTranscriptChanging;
            _viewModel.TranscriptChanged -= OnTranscriptChanged;
            _viewModel.PropertyChanging -= OnViewModelPropertyChanging;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
        }

        DataContext = null;
        _viewModel = null;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) => ApplyHeaderLayout();

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyHeaderLayout();

    private void OnViewModelPropertyChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(AgentChatViewModel.DisplayedSession), StringComparison.Ordinal))
        {
            _transcriptBehavior.MarkInitialPlacementPending();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(AgentChatViewModel.WorkspacePathChipLabels), StringComparison.Ordinal))
        {
            QueueWorkspacePathChipLayoutUpdate();
        }
    }

    private async void CopyTranscriptText_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string content } button || string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            await PublishClipboardNotificationAsync("Clipboard unavailable", "Sunder could not access the system clipboard.", PackageNotificationSeverity.Warning);
            return;
        }

        try
        {
            await clipboard.SetTextAsync(content);
            var target = button.DataContext is AgentTextTranscriptRowViewModel { IsUser: false }
                ? "response"
                : "message";
            await PublishClipboardNotificationAsync("Copied", $"Copied {target} to clipboard.", PackageNotificationSeverity.Success);
        }
        catch
        {
            await PublishClipboardNotificationAsync("Clipboard unavailable", "Sunder could not write to the system clipboard.", PackageNotificationSeverity.Warning);
        }
    }

    private void JumpToLatestTranscript_OnClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        if (!viewModel.HasNewerTranscriptRows)
        {
            _transcriptBehavior.ScrollToBottom();
            return;
        }

        if (viewModel.JumpToLatestTranscriptCommand.CanExecute(null))
        {
            _transcriptBehavior.JumpToLatest(
                () => viewModel.JumpToLatestTranscriptCommand.Execute(null));
        }
    }

    private ValueTask PublishClipboardNotificationAsync(string title, string message, PackageNotificationSeverity severity)
        => _notificationService.PublishAsync(new PackageNotificationRequest(
            title,
            message,
            PackageNotificationDisplayMode.ToastOnly,
            severity));

    private async void OnAttachFilesClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach files",
            AllowMultiple = true,
            FileTypeFilter = [SupportedAttachmentFileType, FilePickerFileTypes.All],
        });
        var paths = files
            .Where(file => file.Path.IsFile)
            .Select(file => file.Path.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        await _viewModel.AddAttachmentPathsAsync(paths);
    }

    private void OnSessionActionsClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || button.DataContext is not AgentSessionListItemViewModel session)
        {
            return;
        }

        var viewModel = _viewModel ?? DataContext as AgentChatViewModel;
        if (viewModel is null)
        {
            return;
        }

        var renameItem = new MenuItem { Header = "Rename" };
        var shouldFocusRename = false;
        renameItem.Click += (_, _) =>
        {
            shouldFocusRename = true;
            viewModel.BeginRenameSessionCommand.Execute(session);
        };

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) => viewModel.DeleteSessionCommand.Execute(session);

        var flyout = new MenuFlyout();
        flyout.Closed += (_, _) =>
        {
            if (!shouldFocusRename || !session.IsRenameActive)
            {
                return;
            }

            _renameFocus.Request(session, reopenDropDown: true);
        };
        flyout.Items.Add(renameItem);
        flyout.Items.Add(deleteItem);
        flyout.ShowAt(button);
    }

    private void OnSessionRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not TextBox { DataContext: AgentSessionListItemViewModel session })
        {
            return;
        }

        var viewModel = _viewModel ?? DataContext as AgentChatViewModel;
        if (viewModel is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            viewModel.SaveSessionRenameCommand.Execute(session);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            viewModel.CancelSessionRenameCommand.Execute(session);
        }
    }

    private static void OnSessionRenameInputInteraction(object? sender, RoutedEventArgs e)
    {
        // Keep clicks and text selection inside the inline editor from selecting the ComboBox row.
        e.Handled = true;
    }

    private void OnSessionRenameTextBoxAttachedToVisualTree(
        object? sender,
        VisualTreeAttachmentEventArgs e
    )
    {
        QueueFocusInlineSessionRenameTextBoxIfPending(sender as TextBox);
    }

    private void OnSessionRenameTextBoxLayoutUpdated(object? sender, EventArgs e)
    {
        QueueFocusInlineSessionRenameTextBoxIfPending(sender as TextBox);
    }

    private void QueueFocusInlineSessionRenameTextBoxIfPending(TextBox? textBox)
        => _renameFocus.TryFocusAttached(textBox);

    private void ConfigureComposerDropTarget(Control control)
    {
        DragDrop.SetAllowDrop(control, true);
        control.AddHandler(DragDrop.DragEnterEvent, OnComposerDragEnter);
        control.AddHandler(DragDrop.DragOverEvent, OnComposerDragOver);
        control.AddHandler(DragDrop.DragLeaveEvent, OnComposerDragLeave);
        control.AddHandler(DragDrop.DropEvent, OnComposerDrop);
    }

    private void ConfigureComposerKeyHandler(TextBox textBox)
    {
        textBox.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
    }

    private async void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not TextBox textBox)
        {
            return;
        }

        if (IsPasteShortcut(e))
        {
            e.Handled = true;
            await PasteClipboardContentAsync(textBox);
            return;
        }

        if (e.Key == Key.Escape && _viewModel?.IsRollbackPending == true)
        {
            e.Handled = true;
            if (_viewModel.CancelRollbackCommand.CanExecute(null))
            {
                _viewModel.CancelRollbackCommand.Execute(null);
            }

            return;
        }

        if (IsSendShortcut(e) && _viewModel?.IsSendOnEnterEnabled == true)
        {
            e.Handled = true;
            if (_viewModel.SendMessageCommand.CanExecute(null))
            {
                await _viewModel.SendMessageCommand.ExecuteAsync(null);
            }
        }
    }

    private async Task PasteClipboardContentAsync(TextBox textBox)
    {
        var paths = await TryGetClipboardFilePathsAsync();
        if (paths.Length > 0)
        {
            if (_viewModel is not null)
            {
                await _viewModel.AddAttachmentPathsAsync(paths);
            }

            return;
        }

        var bitmapUpload = await TryGetClipboardBitmapUploadAsync();
        if (bitmapUpload is not null)
        {
            if (_viewModel is not null)
            {
                await _viewModel.AddAttachmentUploadsAsync([bitmapUpload]);
            }

            return;
        }

        textBox.Paste();
    }

    private async Task<string[]> TryGetClipboardFilePathsAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return [];
        }

        IReadOnlyList<IStorageItem>? storageItems;
        try
        {
            storageItems = await clipboard.TryGetFilesAsync();
        }
        catch
        {
            return [];
        }

        if (storageItems is null || storageItems.Count == 0)
        {
            return [];
        }

        try
        {
            return storageItems
                .OfType<IStorageFile>()
                .Where(file => file.Path.IsFile)
                .Select(file => file.Path.LocalPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
        }
        finally
        {
            foreach (var item in storageItems)
            {
                item.Dispose();
            }
        }
    }

    private async Task<AgentAttachmentUploadRequest?> TryGetClipboardBitmapUploadAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return null;
        }

        Bitmap? bitmap;
        try
        {
            bitmap = await clipboard.TryGetBitmapAsync();
        }
        catch
        {
            return null;
        }

        if (bitmap is null)
        {
            return null;
        }

        using (bitmap)
        using (var stream = new MemoryStream())
        {
            bitmap.Save(stream);
            return new AgentAttachmentUploadRequest(CreateClipboardImageFileName(), "image/png", stream.ToArray());
        }
    }

    private static string CreateClipboardImageFileName()
        => $"clipboard-image-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.png";

    private static bool IsPasteShortcut(KeyEventArgs e)
        => e.Key == Key.V
           && (e.KeyModifiers.HasFlag(KeyModifiers.Control)
               || e.KeyModifiers.HasFlag(KeyModifiers.Meta));

    private static bool IsSendShortcut(KeyEventArgs e)
        => e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None;

    private void OnComposerDragEnter(object? sender, DragEventArgs e)
        => UpdateComposerDragState(e);

    private void OnComposerDragOver(object? sender, DragEventArgs e)
        => UpdateComposerDragState(e);

    private void OnComposerDragLeave(object? sender, DragEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.IsComposerDropTargetActive = false;
        }
    }

    private async void OnComposerDrop(object? sender, DragEventArgs e)
    {
        var paths = GetDroppedFilePaths(e);
        e.DragEffects = paths.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (_viewModel is null)
        {
            return;
        }

        _viewModel.IsComposerDropTargetActive = false;
        if (paths.Length == 0 || !_viewModel.IsSelectedSessionRunInactive)
        {
            return;
        }

        await _viewModel.AddAttachmentPathsAsync(paths);
    }

    private void UpdateComposerDragState(DragEventArgs e)
    {
        var canDrop = _viewModel?.IsSelectedSessionRunInactive == true && GetDroppedFilePaths(e).Length > 0;
        if (_viewModel is not null)
        {
            _viewModel.IsComposerDropTargetActive = canDrop;
        }

        e.DragEffects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static string[] GetDroppedFilePaths(DragEventArgs e)
        => e.DataTransfer.TryGetFiles()?
            .OfType<IStorageFile>()
            .Where(file => file.Path.IsFile)
            .Select(file => file.Path.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray()
            ?? [];

    private void OnTranscriptChanged() => _transcriptBehavior.OnTranscriptChanged();

    private void OnTranscriptChanging()
        => _transcriptBehavior.OnTranscriptChanging(
            ViewModel?.IsLoadingOlderTranscriptRows == true
            || ViewModel?.IsLoadingNewerTranscriptRows == true);

    private void ToolStepHeader_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not AgentToolInvocationRowViewModel toolRow)
        {
            return;
        }

        _transcriptBehavior.MutateViewport(() =>
        {
            toolRow.ToggleExpandedCommand.Execute(null);
            ViewModel?.SetTranscriptRowExpanded(toolRow, toolRow.IsExpanded);
        });
    }

    private void ApplyHeaderLayout()
    {
        var useWideLayout = Bounds.Width >= WideHeaderMinimumWidth;
        HeaderWideLayout.IsVisible = useWideLayout;
        HeaderNarrowLayout.IsVisible = !useWideLayout;
        QueueWorkspacePathChipLayoutUpdate();
    }

    private void OnWorkspacePathRowSizeChanged(object? sender, SizeChangedEventArgs e)
        => QueueWorkspacePathChipLayoutUpdate();

    private void QueueWorkspacePathChipLayoutUpdate()
        => Dispatcher.UIThread.Post(UpdateWorkspacePathChipLayout, DispatcherPriority.Loaded);

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

    private static int CalculateVisibleWorkspacePathChipCount(IReadOnlyList<string> labels, double availableWidth)
    {
        if (labels.Count == 0)
        {
            return 0;
        }

        if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
        {
            return labels.Count;
        }

        for (var count = labels.Count; count >= 0; count--)
        {
            var overflowCount = labels.Count - count;
            var width = MeasureWorkspacePathChipsWidth(labels, count);
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

    private static double MeasureWorkspacePathChipsWidth(IReadOnlyList<string> labels, int count)
    {
        var width = 0d;
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                width += WorkspacePathChipSpacing;
            }

            width += MeasureWorkspacePathTextWidth(labels[index]) + WorkspacePathChipChromeWidth;
        }

        return width;
    }

    private static double MeasureWorkspacePathOverflowWidth(int overflowCount)
        => MeasureWorkspacePathTextWidth($"+{overflowCount} more") + WorkspacePathOverflowChromeWidth;

    private static double MeasureWorkspacePathTextWidth(string text)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontFamily = WorkspacePathChipFontFamily,
            FontSize = WorkspacePathChipTextFontSize,
        };
        textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(textBlock.DesiredSize.Width);
    }
}
