using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentChatView : UserControl
{
    private const double AutoScrollThreshold = 24;
    private const double LoadOlderThreshold = 36;
    private const double WideHeaderMinimumWidth = 520;
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
    private IPackageNotificationService _notificationService = NullPackageNotificationService.Instance;
    private bool _shouldAutoScroll = true;
    private bool _isProgrammaticScroll;
    private bool _scrollToBottomPending;
    private bool _loadOlderPending;

    public AgentChatView()
    {
        InitializeComponent();
        ConfigureComposerDropTarget(ExpandedComposerDropTarget);
        ConfigureComposerDropTarget(ExpandedComposerTextBox);
        ConfigureComposerDropTarget(CollapsedComposerDropTarget);
        ConfigureComposerDropTarget(CollapsedComposerTextBox);
        ConfigureComposerKeyHandler(ExpandedComposerTextBox);
        ConfigureComposerKeyHandler(CollapsedComposerTextBox);
        TranscriptScrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        Loaded += (_, _) =>
        {
            ApplyHeaderLayout();
            QueueScrollToBottom();
        };
        SizeChanged += (_, _) => ApplyHeaderLayout();
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
        _viewModel.TranscriptChanged += OnTranscriptChanged;
        DataContext = _viewModel;
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

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property != ScrollViewer.OffsetProperty)
        {
            return;
        }

        OnScrollOffsetChanged();
    }

    private void OnTranscriptChanged()
    {
        if (!_shouldAutoScroll)
        {
            return;
        }

        QueueScrollToBottom();
    }

    private void OnScrollOffsetChanged()
    {
        if (_isProgrammaticScroll)
        {
            return;
        }

        _shouldAutoScroll = IsNearBottom();
        if (TranscriptScrollViewer.Offset.Y <= LoadOlderThreshold)
        {
            QueueLoadOlderTranscriptRows();
        }
    }

    private void QueueLoadOlderTranscriptRows()
    {
        if (_loadOlderPending || _viewModel?.CanLoadOlderTranscriptRows != true)
        {
            return;
        }

        _loadOlderPending = true;
        var oldExtentHeight = TranscriptScrollViewer.Extent.Height;
        var oldOffset = TranscriptScrollViewer.Offset;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (_viewModel is null)
                {
                    return;
                }

                var loaded = await _viewModel.LoadOlderTranscriptRowsAsync();
                if (!loaded)
                {
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    var addedHeight = Math.Max(0, TranscriptScrollViewer.Extent.Height - oldExtentHeight);
                    _isProgrammaticScroll = true;
                    TranscriptScrollViewer.Offset = new Vector(oldOffset.X, oldOffset.Y + addedHeight);
                    _isProgrammaticScroll = false;
                    _shouldAutoScroll = false;
                }, DispatcherPriority.Background);
            }
            finally
            {
                _loadOlderPending = false;
            }
        }, DispatcherPriority.Background);
    }

    private void QueueScrollToBottom()
    {
        if (_scrollToBottomPending)
        {
            return;
        }

        _scrollToBottomPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollToBottomPending = false;
            ScrollToBottom();
        }, DispatcherPriority.Background);
    }

    private void ScrollToBottom()
    {
        var maxOffsetY = Math.Max(0, TranscriptScrollViewer.Extent.Height - TranscriptScrollViewer.Viewport.Height);
        _isProgrammaticScroll = true;
        TranscriptScrollViewer.Offset = new Vector(TranscriptScrollViewer.Offset.X, maxOffsetY);
        _isProgrammaticScroll = false;
        _shouldAutoScroll = true;
    }

    private bool IsNearBottom()
    {
        var distanceFromBottom = TranscriptScrollViewer.Extent.Height - (TranscriptScrollViewer.Offset.Y + TranscriptScrollViewer.Viewport.Height);
        return distanceFromBottom <= AutoScrollThreshold;
    }

    private void ApplyHeaderLayout()
    {
        var useWideLayout = Bounds.Width >= WideHeaderMinimumWidth;
        HeaderWideLayout.IsVisible = useWideLayout;
        HeaderNarrowLayout.IsVisible = !useWideLayout;
    }
}
