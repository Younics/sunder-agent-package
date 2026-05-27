using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
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

public partial class AgentChatView : UserControl
{
    private const double WideHeaderMinimumWidth = 520;
    private const int SessionRenameFocusRetryLimit = 12;
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
    private TranscriptScrollCoordinator? _transcriptScrollCoordinator;
    private bool _transcriptChangedBeforeScrollReady;
    private bool _initialTranscriptPlacementPending = true;
    private bool _initialTranscriptPlacementQueued;
    private int _initialTranscriptPlacementVersion;
    private Guid? _pendingSessionRenameFocusId;
    private IPackageNotificationService _notificationService = NullPackageNotificationService.Instance;

    public AgentChatView()
    {
        InitializeComponent();
        HideTranscriptUntilInitialPlacement();
        ConfigureComposerDropTarget(ExpandedComposerDropTarget);
        ConfigureComposerDropTarget(ExpandedComposerTextBox);
        ConfigureComposerDropTarget(CollapsedComposerDropTarget);
        ConfigureComposerDropTarget(CollapsedComposerTextBox);
        ConfigureComposerKeyHandler(ExpandedComposerTextBox);
        ConfigureComposerKeyHandler(CollapsedComposerTextBox);
        Loaded += (_, _) =>
        {
            ApplyHeaderLayout();
            if (EnsureTranscriptScrollCoordinator())
            {
                HandleTranscriptReadyAfterScrollReady();
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (EnsureTranscriptScrollCoordinator())
                {
                    HandleTranscriptReadyAfterScrollReady();
                }
            }, DispatcherPriority.Loaded);
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
        _viewModel.TranscriptChanging += OnTranscriptChanging;
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
            _transcriptScrollCoordinator?.QueueScrollToBottom();
            return;
        }

        if (!_viewModel.JumpToLatestTranscriptCommand.CanExecute(null))
        {
            return;
        }

        _transcriptScrollCoordinator?.ForceScrollToBottomOnNextTranscriptChanged();
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
            _pendingSessionRenameFocusId = session.SessionId;
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

            _pendingSessionRenameFocusId = session.SessionId;
            QueueFocusInlineSessionRenameTextBox(session, reopenDropdown: true);
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
    {
        if (
            textBox?.DataContext is not AgentSessionListItemViewModel session
            || !session.IsRenameActive
            || _pendingSessionRenameFocusId != session.SessionId
            || !textBox.IsEffectivelyVisible
        )
        {
            return;
        }

        QueueFocusInlineSessionRenameTextBox(textBox, session);
    }

    private void QueueFocusInlineSessionRenameTextBox(
        AgentSessionListItemViewModel session,
        bool reopenDropdown,
        int attempt = 0
    )
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_pendingSessionRenameFocusId != session.SessionId || !session.IsRenameActive)
            {
                return;
            }

            if (reopenDropdown)
            {
                OpenVisibleSessionDropDown();
            }

            var textBox = FindInlineSessionRenameTextBox(session);
            if (textBox is not null)
            {
                QueueFocusInlineSessionRenameTextBox(textBox, session, attempt);
                return;
            }

            if (attempt < SessionRenameFocusRetryLimit)
            {
                QueueFocusInlineSessionRenameTextBox(session, reopenDropdown: false, attempt + 1);
            }
        }, DispatcherPriority.Background);
    }

    private void OpenVisibleSessionDropDown()
    {
        var comboBox = HeaderWideLayout.IsVisible ? WideSessionComboBox : NarrowSessionComboBox;
        comboBox.IsDropDownOpen = true;
    }

    private void QueueFocusInlineSessionRenameTextBox(
        TextBox textBox,
        AgentSessionListItemViewModel session,
        int attempt = 0
    )
    {
        Dispatcher.UIThread.Post(
            () => FocusInlineSessionRenameTextBox(textBox, session, attempt),
            DispatcherPriority.ContextIdle
        );
    }

    private void FocusInlineSessionRenameTextBox(
        TextBox textBox,
        AgentSessionListItemViewModel session,
        int attempt
    )
    {
        if (_pendingSessionRenameFocusId != session.SessionId || !session.IsRenameActive)
        {
            return;
        }

        if (textBox.IsEffectivelyVisible)
        {
            TopLevel.GetTopLevel(textBox)?.FocusManager?.Focus(
                textBox,
                NavigationMethod.Unspecified,
                KeyModifiers.None
            );
            textBox.Focus();
            var caretIndex = textBox.Text?.Length ?? 0;
            textBox.CaretIndex = caretIndex;
            textBox.SelectionStart = caretIndex;
            textBox.SelectionEnd = caretIndex;
        }

        Dispatcher.UIThread.Post(
            () => VerifyInlineSessionRenameTextBoxFocus(textBox, session, attempt),
            DispatcherPriority.ContextIdle
        );
    }

    private void VerifyInlineSessionRenameTextBoxFocus(
        TextBox textBox,
        AgentSessionListItemViewModel session,
        int attempt
    )
    {
        if (_pendingSessionRenameFocusId != session.SessionId || !session.IsRenameActive)
        {
            return;
        }

        if (textBox.IsKeyboardFocusWithin)
        {
            _pendingSessionRenameFocusId = null;
            return;
        }

        if (attempt < SessionRenameFocusRetryLimit)
        {
            QueueFocusInlineSessionRenameTextBox(textBox, session, attempt + 1);
        }
    }

    private TextBox? FindInlineSessionRenameTextBox(AgentSessionListItemViewModel session)
    {
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            var textBox = FindInlineSessionRenameTextBox(topLevel, session);
            if (textBox is not null)
            {
                return textBox;
            }
        }

        return FindInlineSessionRenameTextBox(this, session);
    }

    private static TextBox? FindInlineSessionRenameTextBox(
        Visual root,
        AgentSessionListItemViewModel session
    )
        => root.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(textBox =>
                ReferenceEquals(textBox.DataContext, session)
                && textBox.Classes.Contains("session-rename-input")
            );

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

    private void OnTranscriptChanged()
    {
        if (!EnsureTranscriptScrollCoordinator())
        {
            _transcriptChangedBeforeScrollReady = true;
            return;
        }

        if (TryHandleInitialTranscriptPlacement())
        {
            return;
        }

        _transcriptScrollCoordinator?.OnTranscriptChanged();
    }

    private void OnTranscriptChanging()
    {
        var viewModel = _viewModel ?? DataContext as AgentChatViewModel;
        if (_initialTranscriptPlacementPending || viewModel?.IsTranscriptLoading == true)
        {
            return;
        }

        if (viewModel?.IsLoadingOlderTranscriptRows == true || viewModel?.IsLoadingNewerTranscriptRows == true)
        {
            _transcriptScrollCoordinator?.DiscardPendingTranscriptMutation();
            return;
        }

        if (!EnsureTranscriptScrollCoordinator())
        {
            return;
        }

        _transcriptScrollCoordinator?.BeginTranscriptMutation();
    }

    private bool EnsureTranscriptScrollCoordinator()
    {
        if (_transcriptScrollCoordinator is not null)
        {
            return true;
        }

        _transcriptScrollCoordinator = new TranscriptScrollCoordinator(
            TranscriptScrollViewer,
            TranscriptItemsControl,
            () => _viewModel?.CanLoadOlderTranscriptRows == true,
            anchorKey => _viewModel?.LoadOlderTranscriptRowsAsync(anchorKey) ?? Task.FromResult(false),
            () => _viewModel?.CanLoadNewerTranscriptRows == true,
            anchorKey => _viewModel?.LoadNewerTranscriptRowsAsync(anchorKey) ?? Task.FromResult(false),
            () => _viewModel?.HasNewerTranscriptRows == true,
            isVisible => JumpToLatestTranscriptButton.IsVisible = isVisible,
            () => _viewModel?.DetachTranscriptFromLatest(),
            () => _viewModel?.ResumeTranscriptFollowingLatestIfCaughtUp());
        return true;
    }

    private void HandleTranscriptReadyAfterScrollReady()
    {
        if (TryHandleInitialTranscriptPlacement())
        {
            return;
        }

        if (_transcriptChangedBeforeScrollReady)
        {
            _transcriptChangedBeforeScrollReady = false;
            _transcriptScrollCoordinator?.OnTranscriptChanged();
        }
    }

    private bool TryHandleInitialTranscriptPlacement()
    {
        var viewModel = _viewModel ?? DataContext as AgentChatViewModel;
        if (viewModel?.IsTranscriptLoading == true)
        {
            MarkInitialTranscriptPlacementPending();
            return true;
        }

        if (!_initialTranscriptPlacementPending)
        {
            return false;
        }

        _transcriptChangedBeforeScrollReady = false;
        if (viewModel is null || viewModel.ShowSetupInstructions || viewModel.Messages.Count == 0 || !TranscriptScrollViewer.IsVisible)
        {
            CompleteInitialTranscriptPlacement(_initialTranscriptPlacementVersion);
            return true;
        }

        if (_initialTranscriptPlacementQueued)
        {
            return true;
        }

        _initialTranscriptPlacementQueued = true;
        var placementVersion = _initialTranscriptPlacementVersion;
        HideTranscriptUntilInitialPlacement();
        _transcriptScrollCoordinator?.QueueScrollToBottomAfterLayoutSettles(() => CompleteInitialTranscriptPlacement(placementVersion));
        return true;
    }

    private void MarkInitialTranscriptPlacementPending()
    {
        if (!_initialTranscriptPlacementPending || _initialTranscriptPlacementQueued)
        {
            _initialTranscriptPlacementVersion++;
        }

        _initialTranscriptPlacementPending = true;
        _initialTranscriptPlacementQueued = false;
        HideTranscriptUntilInitialPlacement();
    }

    private void HideTranscriptUntilInitialPlacement()
        => TranscriptScrollViewer.Opacity = 0;

    private void CompleteInitialTranscriptPlacement(int placementVersion)
    {
        if (placementVersion != _initialTranscriptPlacementVersion)
        {
            return;
        }

        _initialTranscriptPlacementPending = false;
        _initialTranscriptPlacementQueued = false;
        TranscriptScrollViewer.Opacity = 1;
    }

    private void ToolStepHeader_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not AgentToolInvocationRowViewModel toolRow)
        {
            return;
        }

        if (EnsureTranscriptScrollCoordinator())
        {
            _transcriptScrollCoordinator?.BeginViewportMutation();
        }

        toolRow.ToggleExpandedCommand.Execute(null);
        _transcriptScrollCoordinator?.OnViewportContentChanged();
    }

    private void ApplyHeaderLayout()
    {
        var useWideLayout = Bounds.Width >= WideHeaderMinimumWidth;
        HeaderWideLayout.IsVisible = useWideLayout;
        HeaderNarrowLayout.IsVisible = !useWideLayout;
    }
}
