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
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentChatView : UserControl,
    IDisposable,
    IPackageViewNavigationTarget,
    IPackageViewNavigationPreparationTarget
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
    private readonly PresentationTaskScope _tasks;
    private readonly AdaptiveEditorStateCache<AgentChatEditorState> _editorStateCache = new();
    private readonly Dictionary<string, double> _workspacePathTextWidths = new(StringComparer.Ordinal);
    private readonly object _workspacePathLayoutSyncRoot = new();
    private IPackageNotificationService _notificationService = NullPackageNotificationService.Instance;
    private CancellationTokenSource? _navigationCancellation;
    private CancellationTokenSource? _toolExpansionCancellation;
    private PreparedAgentHistoryNavigation? _preparedHistoryNavigation;
    private int _navigationGeneration;
    private bool _workspacePathLayoutQueued;
    private bool _workspacePathLayoutDirty;
    private bool _headerLayoutInitialized;
    private bool _usesWideLayout;
    private bool _lastComposerExpanded;
    private bool _disposed;

    public AgentChatView()
    {
        _tasks = new PresentationTaskScope();
        InitializeComponent();
        ConfigureComposerDropTarget(ExpandedComposerDropTarget);
        ConfigureComposerDropTarget(ExpandedComposerTextBox);
        ConfigureComposerDropTarget(CollapsedComposerDropTarget);
        ConfigureComposerDropTarget(CollapsedComposerTextBox);
        ConfigureComposerKeyHandler(ExpandedComposerTextBox);
        ConfigureComposerKeyHandler(CollapsedComposerTextBox);
        _transcriptBehavior = new TranscriptViewBehavior(
            this,
            TranscriptScrollViewer,
            TranscriptItemsControl,
            JumpToLatestTranscriptButton,
            () => ViewModel?.CanLoadOlderTranscriptRows == true,
            (anchor, cancellationToken) => ViewModel?.LoadOlderTranscriptRowsAsync(anchor, cancellationToken)
                                           ?? Task.FromResult(false),
            () => ViewModel?.CanLoadNewerTranscriptRows == true,
            (anchor, cancellationToken) => ViewModel?.LoadNewerTranscriptRowsAsync(
                                                anchor,
                                                cancellationToken,
                                                resumeFollowingWhenCaughtUp: false)
                                            ?? Task.FromResult(false),
            () => ViewModel?.HasNewerTranscriptRows == true,
            () => ViewModel?.IsTranscriptFollowingLatest != false,
            () => ViewModel?.IsTranscriptLoading == true,
            () => ViewModel?.Messages.Count > 0,
            () => ViewModel is { ShowSetupInstructions: false },
            () => ViewModel?.DetachTranscriptFromLatest() == true,
            () => ViewModel?.ResumeTranscriptFollowingLatestIfCaughtUp() == true,
            isVisible => ViewModel?.SetTranscriptJumpToLatestVisible(isVisible),
            anchor => ViewModel?.SetTranscriptViewportAnchor(anchor),
            () => ViewModel?.TranscriptViewportAnchor,
            exception => ViewModel?.ReportTranscriptPagingFailure(exception),
            enumerateRealizedAnchors: EnumerateRealizedTranscriptAnchors,
            realizeAnchorVisual: RealizeTranscriptAnchor,
            realizeTailVisual: RealizeTranscriptTailSentinel,
            presentationStateChanged: isActive => ViewModel?.SetTranscriptPresentationActive(isActive),
            anchorHost: TranscriptAnchorHost);
        _renameFocus = new InlineRenameFocusCoordinator<AgentSessionListItemViewModel>(
            this,
            session => session.SessionId,
            session => session.IsRenameActive,
            () => HeaderWideLayout.IsVisible ? WideSessionComboBox : NarrowSessionComboBox,
            "session-rename-input");
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    public AgentChatView(
        IAgentProfileGateway profileService,
        IAgentWorkspaceGateway workspaceService,
        IAgentSessionGateway sessionService,
        IAgentPermissionGateway permissionService,
        IAgentRunGateway runCoordinator,
        AgentChatSelectionStateService selectionState,
        AgentToolPresentationService toolPresentationService,
        IAgentExecutionGateway warmupService,
        IPackageShellViewService shellViewService,
        IAgentAttachmentGateway attachmentService,
        IPackageNotificationService notificationService)
        : this()
    {
        _notificationService = notificationService;
        var viewModel = new AgentChatViewModel(
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
        if (sessionService is IAgentTranscriptAnchorGateway transcriptAnchorGateway)
        {
            viewModel.SetTranscriptAnchorGateway(transcriptAnchorGateway);
        }
        AttachViewModel(viewModel);
    }

    internal AgentChatView(AgentChatViewModel viewModel)
        : this()
        => AttachViewModel(viewModel);

    private AgentChatViewModel? ViewModel => _viewModel ?? DataContext as AgentChatViewModel;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = null;
        CancelPendingToolExpansion();
        ToolDetailPreparationPortal.Dispose();
        Loaded -= OnLoaded;
        SizeChanged -= OnSizeChanged;
        _transcriptBehavior.Dispose();
        _renameFocus.Dispose();
        _tasks.Dispose();
        if (_viewModel is not null)
        {
            _viewModel.TranscriptChanging -= OnTranscriptChanging;
            _viewModel.TranscriptChanged -= OnTranscriptChanged;
            _viewModel.TranscriptTailFollowRequested -= OnTranscriptTailFollowRequested;
            _viewModel.PropertyChanging -= OnViewModelPropertyChanging;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
        }

        DataContext = null;
        _viewModel = null;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) => ApplyHeaderLayout();

    private void AttachViewModel(AgentChatViewModel viewModel)
    {
        _viewModel = viewModel;
        _viewModel.TranscriptChanging += OnTranscriptChanging;
        _viewModel.TranscriptChanged += OnTranscriptChanged;
        _viewModel.TranscriptTailFollowRequested += OnTranscriptTailFollowRequested;
        _viewModel.PropertyChanging += OnViewModelPropertyChanging;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = _viewModel;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        => _transcriptBehavior.MutateViewport(
            () => ApplyHeaderLayout(e.NewSize.Width),
            TranscriptViewportMutationKind.StructuralLayout);

    private void OnViewModelPropertyChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(AgentChatViewModel.DisplayedSession), StringComparison.Ordinal))
        {
            _transcriptBehavior.MarkInitialPlacementPending(_navigationCancellation?.Token ?? default);
        }

        if (string.Equals(e.PropertyName, nameof(AgentChatViewModel.IsComposerExpanded), StringComparison.Ordinal))
        {
            CaptureComposerEditorState(_usesWideLayout, _lastComposerExpanded);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(AgentChatViewModel.WorkspacePathChipLabels), StringComparison.Ordinal))
        {
            QueueWorkspacePathChipLayoutUpdate();
        }
        else if (string.Equals(e.PropertyName, nameof(AgentChatViewModel.IsComposerExpanded), StringComparison.Ordinal))
        {
            _lastComposerExpanded = ViewModel?.IsComposerExpanded == true;
            QueueRestoreComposerEditorState();
        }
    }

    private void CopyTranscriptText_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string content } button || string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        _tasks.Run(_ => CopyTranscriptTextAsync(button, content));
    }

    private async Task CopyTranscriptTextAsync(Button button, string content)
    {
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

        if (viewModel.JumpToLatestTranscriptCommand.CanExecute(null))
        {
            viewModel.JumpToLatestTranscriptCommand.Execute(null);
        }
    }

    private void OnTranscriptTailFollowRequested(Guid sessionId)
    {
        if (ViewModel?.DisplayedTranscriptSessionId == sessionId)
        {
            _transcriptBehavior.FollowLatestFromExplicitIntent();
        }
    }

    private void TranscriptMarkdown_OnRendered(object? sender, EventArgs e)
        => _transcriptBehavior.OnRenderedContentChanged(sender as ITranscriptGeometrySource);

    private ValueTask PublishClipboardNotificationAsync(string title, string message, PackageNotificationSeverity severity)
        => _notificationService.PublishAsync(new PackageNotificationRequest(
            title,
            message,
            PackageNotificationDisplayMode.ToastOnly,
            severity));

    private void OnAttachFilesClick(object? sender, RoutedEventArgs e)
        => _tasks.Run(OnAttachFilesAsync);

    private async Task OnAttachFilesAsync(CancellationToken cancellationToken)
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
        cancellationToken.ThrowIfCancellationRequested();
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

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not TextBox textBox)
        {
            return;
        }

        if (IsPasteShortcut(e))
        {
            e.Handled = true;
            _tasks.Run(_ => PasteClipboardContentAsync(textBox));
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
                _tasks.Run(_ => _viewModel.SendMessageCommand.ExecuteAsync(null));
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

    private void OnComposerDrop(object? sender, DragEventArgs e)
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

        _tasks.Run(_ => _viewModel.AddAttachmentPathsAsync(paths));
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

    private void OnTranscriptChanging(bool isPageApplication)
    {
        // Keyed page application retains the active row; expansion currentness handles actual replacement.
        if (!isPageApplication)
        {
            CancelPendingToolExpansion();
        }
        _transcriptBehavior.OnTranscriptChanging(isPageApplication);
    }

}
