using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentChatView : UserControl
{
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
    private readonly TranscriptScrollCoordinator _transcriptScrollCoordinator;
    private IPackageNotificationService _notificationService = NullPackageNotificationService.Instance;

    public AgentChatView()
    {
        InitializeComponent();
        _transcriptScrollCoordinator = new TranscriptScrollCoordinator(
            TranscriptScrollViewer,
            () => _viewModel?.CanLoadOlderTranscriptRows == true,
            () => _viewModel?.LoadOlderTranscriptRowsAsync() ?? Task.FromResult(false),
            () => _viewModel?.CanLoadNewerTranscriptRows == true,
            () => _viewModel?.LoadNewerTranscriptRowsAsync() ?? Task.FromResult(false),
            () => _viewModel?.HasNewerTranscriptRows == true,
            isVisible => JumpToLatestTranscriptButton.IsVisible = isVisible);
        ConfigureComposerDropTarget(ExpandedComposerDropTarget);
        ConfigureComposerDropTarget(ExpandedComposerTextBox);
        ConfigureComposerDropTarget(CollapsedComposerDropTarget);
        ConfigureComposerDropTarget(CollapsedComposerTextBox);
        ConfigureComposerKeyHandler(ExpandedComposerTextBox);
        ConfigureComposerKeyHandler(CollapsedComposerTextBox);
        Loaded += (_, _) =>
        {
            ApplyHeaderLayout();
            _transcriptScrollCoordinator.QueueScrollToBottom();
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

    private void JumpToLatestTranscript_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (!_viewModel.HasNewerTranscriptRows)
        {
            _transcriptScrollCoordinator.QueueScrollToBottom();
            return;
        }

        if (!_viewModel.JumpToLatestTranscriptCommand.CanExecute(null))
        {
            return;
        }

        _transcriptScrollCoordinator.ForceScrollToBottomOnNextTranscriptChanged();
        _viewModel.JumpToLatestTranscriptCommand.Execute(null);
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

    private void OnTranscriptChanged()
        => _transcriptScrollCoordinator.OnTranscriptChanged();

    private void ApplyHeaderLayout()
    {
        var useWideLayout = Bounds.Width >= WideHeaderMinimumWidth;
        HeaderWideLayout.IsVisible = useWideLayout;
        HeaderNarrowLayout.IsVisible = !useWideLayout;
    }
}
