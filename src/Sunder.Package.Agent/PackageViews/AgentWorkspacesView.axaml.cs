using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentWorkspacesView : UserControl, IDisposable
{
    private const int WorkspaceInlineEditFocusRetryLimit = 12;
    private static readonly FilePickerFileType WorkspaceDocumentFileType = new("Workspace documents")
    {
        Patterns = ["*.md", "*.mdx", "*.txt"],
    };

    private AgentWorkspacesViewModel? _viewModel;
    private readonly AdaptiveMasterDetail _adaptiveLayout;
    private readonly AgentWorkspacesViewContext _context;
    private bool _disposed;

    public AgentWorkspacesView()
        : this(new AgentWorkspacesViewContext())
    {
    }

    private AgentWorkspacesView(AgentWorkspacesViewContext context)
    {
        _context = context;
        _context.Failed += OnContextFailed;
        InitializeComponent();
        _adaptiveLayout = new AdaptiveMasterDetail(
            this,
            WorkspaceAdaptiveLayout,
            WorkspaceListPane,
            WorkspaceEditorPane,
            isCompact =>
            {
                var viewModel = _viewModel ?? DataContext as AgentWorkspacesViewModel;
                if (viewModel is not null)
                {
                    viewModel.IsCompactLayout = isCompact;
                }
            });
    }

    public AgentWorkspacesView(
        IAgentWorkspaceGateway workspaceService,
        IAgentExecutionGateway executionGateway,
        IPackageExtensionCatalog extensionCatalog,
        IPackageSettingsNavigationService? settingsNavigationService = null,
        AgentWorkspacesViewContext? context = null)
        : this(context ?? new AgentWorkspacesViewContext())
    {
        _viewModel = new AgentWorkspacesViewModel(workspaceService, executionGateway, extensionCatalog, settingsNavigationService);
        DataContext = _viewModel;
    }

    public AgentWorkspacesView(
        AgentWorkspaceService workspaceService,
        AgentExecutionTargetService executionTargetService,
        IPackageExtensionCatalog extensionCatalog,
        AgentExecutionTargetWarmupService warmupService,
        IPackageSettingsNavigationService? settingsNavigationService = null)
        : this(workspaceService, warmupService, extensionCatalog, settingsNavigationService)
    {
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _adaptiveLayout.Dispose();
        _context.Failed -= OnContextFailed;
        _context.Dispose();
        _viewModel?.Dispose();
        DataContext = null;
        _viewModel = null;
    }

    private void OnAddEditorPathItemClick(object? sender, RoutedEventArgs e)
        => _context.Run(cancellationToken => AddEditorPathItemAsync(sender, cancellationToken));

    private async Task AddEditorPathItemAsync(object? sender, CancellationToken cancellationToken)
    {
        if ((sender as Control)?.DataContext is not AgentEditorPathListFieldViewModel field)
        {
            return;
        }

        if (!field.UseFolderPicker)
        {
            field.AddDefaultItem();
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select workspace path",
            AllowMultiple = false,
        });
        cancellationToken.ThrowIfCancellationRequested();

        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            field.AddItem(folder.Path.LocalPath);
        }
    }

    private void OnAddWorkspacePathClick(object? sender, RoutedEventArgs e)
        => _context.Run(AddWorkspacePathAsync);

    private async Task AddWorkspacePathAsync(CancellationToken cancellationToken)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select workspace path",
            AllowMultiple = false,
        });
        cancellationToken.ThrowIfCancellationRequested();

        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            (_viewModel ?? DataContext as AgentWorkspacesViewModel)?.AddWorkspacePath(folder.Path.LocalPath);
        }
    }

    private void OnAddWorkspaceDocumentClick(object? sender, RoutedEventArgs e)
        => _context.Run(AddWorkspaceDocumentsAsync);

    private async Task AddWorkspaceDocumentsAsync(CancellationToken cancellationToken)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select workspace docs",
            AllowMultiple = true,
            FileTypeFilter = [WorkspaceDocumentFileType, FilePickerFileTypes.All],
        });
        cancellationToken.ThrowIfCancellationRequested();

        var viewModel = _viewModel ?? DataContext as AgentWorkspacesViewModel;
        if (viewModel is null)
        {
            return;
        }

        foreach (var file in files.Where(file => file.Path.IsFile))
        {
            viewModel.AddWorkspaceDocument(file.Path.LocalPath);
        }
    }

    private void OnWorkspacePathDragOver(object? sender, DragEventArgs e)
        => UpdateWorkspaceDrop(e, requireDirectory: true);

    private void OnWorkspacePathDrop(object? sender, DragEventArgs e)
    {
        var paths = GetDroppedStoragePaths(e).Where(Directory.Exists).ToArray();
        e.DragEffects = paths.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        var viewModel = _viewModel ?? DataContext as AgentWorkspacesViewModel;
        if (viewModel is null)
        {
            return;
        }

        foreach (var path in paths)
        {
            viewModel.AddWorkspacePath(path);
        }
    }

    private void OnWorkspaceDocumentDragOver(object? sender, DragEventArgs e)
        => UpdateWorkspaceDrop(e, requireDirectory: false);

    private void OnWorkspaceDocumentDrop(object? sender, DragEventArgs e)
    {
        var paths = GetDroppedStoragePaths(e).Where(File.Exists).ToArray();
        e.DragEffects = paths.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        var viewModel = _viewModel ?? DataContext as AgentWorkspacesViewModel;
        if (viewModel is null)
        {
            return;
        }

        foreach (var path in paths)
        {
            viewModel.AddWorkspaceDocument(path);
        }
    }

    private void OnWorkspacePathActionsClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || button.DataContext is not AgentWorkspacePathItemViewModel path)
        {
            return;
        }

        var viewModel = _viewModel ?? DataContext as AgentWorkspacesViewModel;
        if (viewModel is null)
        {
            return;
        }

        var editItem = new MenuItem { Header = "Edit" };
        var shouldFocusEdit = false;
        editItem.Click += (_, _) =>
        {
            shouldFocusEdit = true;
            _context.PendingPathEditId = path.PathId;
            viewModel.BeginEditWorkspacePathCommand.Execute(path);
        };

        var setDefaultItem = new MenuItem
        {
            Header = path.IsDefault ? "Default" : "Set as default",
            IsEnabled = !path.IsDefault,
        };
        setDefaultItem.Click += (_, _) => viewModel.SetWorkspacePathAsDefaultCommand.Execute(path);

        var removeItem = new MenuItem { Header = "Remove" };
        removeItem.Click += (_, _) => viewModel.DeleteWorkspacePathCommand.Execute(path);

        var flyout = new MenuFlyout();
        flyout.Closed += (_, _) =>
        {
            if (!shouldFocusEdit || !path.IsEditActive)
            {
                return;
            }

            _context.PendingPathEditId = path.PathId;
            QueueFocusInlineWorkspacePathTextBox(path);
        };
        flyout.Items.Add(editItem);
        flyout.Items.Add(setDefaultItem);
        flyout.Items.Add(removeItem);
        flyout.ShowAt(button);
    }

    private void OnWorkspaceDocumentActionsClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button button || button.DataContext is not AgentWorkspaceDocumentItemViewModel document)
        {
            return;
        }

        var viewModel = _viewModel ?? DataContext as AgentWorkspacesViewModel;
        if (viewModel is null)
        {
            return;
        }

        var editItem = new MenuItem { Header = "Edit" };
        var shouldFocusEdit = false;
        editItem.Click += (_, _) =>
        {
            shouldFocusEdit = true;
            _context.PendingDocumentEditId = document.DocumentId;
            viewModel.BeginEditWorkspaceDocumentCommand.Execute(document);
        };

        var removeItem = new MenuItem { Header = "Remove" };
        removeItem.Click += (_, _) => viewModel.DeleteWorkspaceDocumentCommand.Execute(document);

        var flyout = new MenuFlyout();
        flyout.Closed += (_, _) =>
        {
            if (!shouldFocusEdit || !document.IsEditActive)
            {
                return;
            }

            _context.PendingDocumentEditId = document.DocumentId;
            QueueFocusInlineWorkspaceDocumentTextBox(document);
        };
        flyout.Items.Add(editItem);
        flyout.Items.Add(removeItem);
        flyout.ShowAt(button);
    }

    private void OnWorkspacePathEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not TextBox { DataContext: AgentWorkspacePathItemViewModel path })
        {
            return;
        }

        var viewModel = _viewModel ?? DataContext as AgentWorkspacesViewModel;
        if (viewModel is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _context.PendingPathEditId = null;
            viewModel.SaveWorkspacePathEditCommand.Execute(path);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _context.PendingPathEditId = null;
            viewModel.CancelWorkspacePathEditCommand.Execute(path);
        }
    }

    private void OnWorkspaceDocumentEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not TextBox { DataContext: AgentWorkspaceDocumentItemViewModel document })
        {
            return;
        }

        var viewModel = _viewModel ?? DataContext as AgentWorkspacesViewModel;
        if (viewModel is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _context.PendingDocumentEditId = null;
            viewModel.SaveWorkspaceDocumentEditCommand.Execute(document);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _context.PendingDocumentEditId = null;
            viewModel.CancelWorkspaceDocumentEditCommand.Execute(document);
        }
    }

    private static void OnWorkspaceInlineEditInputInteraction(object? sender, RoutedEventArgs e)
        => e.Handled = true;

    private void OnWorkspacePathEditTextBoxAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        => QueueFocusInlineWorkspacePathTextBoxIfPending(sender as TextBox);

    private void OnWorkspacePathEditTextBoxLayoutUpdated(object? sender, EventArgs e)
        => QueueFocusInlineWorkspacePathTextBoxIfPending(sender as TextBox);

    private void OnWorkspaceDocumentEditTextBoxAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        => QueueFocusInlineWorkspaceDocumentTextBoxIfPending(sender as TextBox);

    private void OnWorkspaceDocumentEditTextBoxLayoutUpdated(object? sender, EventArgs e)
        => QueueFocusInlineWorkspaceDocumentTextBoxIfPending(sender as TextBox);

    private void QueueFocusInlineWorkspacePathTextBoxIfPending(TextBox? textBox)
    {
        if (textBox?.DataContext is not AgentWorkspacePathItemViewModel path
            || !path.IsEditActive
            || _context.PendingPathEditId != path.PathId
            || !textBox.IsEffectivelyVisible)
        {
            return;
        }

        QueueFocusInlineWorkspacePathTextBox(textBox, path);
    }

    private void QueueFocusInlineWorkspaceDocumentTextBoxIfPending(TextBox? textBox)
    {
        if (textBox?.DataContext is not AgentWorkspaceDocumentItemViewModel document
            || !document.IsEditActive
            || _context.PendingDocumentEditId != document.DocumentId
            || !textBox.IsEffectivelyVisible)
        {
            return;
        }

        QueueFocusInlineWorkspaceDocumentTextBox(textBox, document);
    }

    private void QueueFocusInlineWorkspacePathTextBox(AgentWorkspacePathItemViewModel path, int attempt = 0)
    {
        _context.Queue(() =>
        {
            if (_context.PendingPathEditId != path.PathId || !path.IsEditActive)
            {
                return;
            }

            var textBox = FindInlineWorkspacePathTextBox(path);
            if (textBox is not null)
            {
                QueueFocusInlineWorkspacePathTextBox(textBox, path, attempt);
                return;
            }

            if (attempt < WorkspaceInlineEditFocusRetryLimit)
            {
                QueueFocusInlineWorkspacePathTextBox(path, attempt + 1);
            }
        }, DispatcherPriority.Background);
    }

    private void QueueFocusInlineWorkspaceDocumentTextBox(AgentWorkspaceDocumentItemViewModel document, int attempt = 0)
    {
        _context.Queue(() =>
        {
            if (_context.PendingDocumentEditId != document.DocumentId || !document.IsEditActive)
            {
                return;
            }

            var textBox = FindInlineWorkspaceDocumentTextBox(document);
            if (textBox is not null)
            {
                QueueFocusInlineWorkspaceDocumentTextBox(textBox, document, attempt);
                return;
            }

            if (attempt < WorkspaceInlineEditFocusRetryLimit)
            {
                QueueFocusInlineWorkspaceDocumentTextBox(document, attempt + 1);
            }
        }, DispatcherPriority.Background);
    }

    private void QueueFocusInlineWorkspacePathTextBox(
        TextBox textBox,
        AgentWorkspacePathItemViewModel path,
        int attempt = 0)
    {
        _context.Queue(
            () => FocusInlineWorkspacePathTextBox(textBox, path, attempt),
            DispatcherPriority.ContextIdle);
    }

    private void QueueFocusInlineWorkspaceDocumentTextBox(
        TextBox textBox,
        AgentWorkspaceDocumentItemViewModel document,
        int attempt = 0)
    {
        _context.Queue(
            () => FocusInlineWorkspaceDocumentTextBox(textBox, document, attempt),
            DispatcherPriority.ContextIdle);
    }

    private void FocusInlineWorkspacePathTextBox(
        TextBox textBox,
        AgentWorkspacePathItemViewModel path,
        int attempt)
    {
        if (_context.PendingPathEditId != path.PathId || !path.IsEditActive)
        {
            return;
        }

        FocusInlineWorkspaceEditTextBox(textBox);
        _context.Queue(
            () => VerifyInlineWorkspacePathTextBoxFocus(textBox, path, attempt),
            DispatcherPriority.ContextIdle);
    }

    private void FocusInlineWorkspaceDocumentTextBox(
        TextBox textBox,
        AgentWorkspaceDocumentItemViewModel document,
        int attempt)
    {
        if (_context.PendingDocumentEditId != document.DocumentId || !document.IsEditActive)
        {
            return;
        }

        FocusInlineWorkspaceEditTextBox(textBox);
        _context.Queue(
            () => VerifyInlineWorkspaceDocumentTextBoxFocus(textBox, document, attempt),
            DispatcherPriority.ContextIdle);
    }

    private static void FocusInlineWorkspaceEditTextBox(TextBox textBox)
    {
        if (!textBox.IsEffectivelyVisible)
        {
            return;
        }

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

    private void VerifyInlineWorkspacePathTextBoxFocus(
        TextBox textBox,
        AgentWorkspacePathItemViewModel path,
        int attempt)
    {
        if (_context.PendingPathEditId != path.PathId || !path.IsEditActive)
        {
            return;
        }

        if (textBox.IsKeyboardFocusWithin)
        {
            _context.PendingPathEditId = null;
            return;
        }

        if (attempt < WorkspaceInlineEditFocusRetryLimit)
        {
            QueueFocusInlineWorkspacePathTextBox(textBox, path, attempt + 1);
        }
    }

    private void VerifyInlineWorkspaceDocumentTextBoxFocus(
        TextBox textBox,
        AgentWorkspaceDocumentItemViewModel document,
        int attempt)
    {
        if (_context.PendingDocumentEditId != document.DocumentId || !document.IsEditActive)
        {
            return;
        }

        if (textBox.IsKeyboardFocusWithin)
        {
            _context.PendingDocumentEditId = null;
            return;
        }

        if (attempt < WorkspaceInlineEditFocusRetryLimit)
        {
            QueueFocusInlineWorkspaceDocumentTextBox(textBox, document, attempt + 1);
        }
    }

    private TextBox? FindInlineWorkspacePathTextBox(AgentWorkspacePathItemViewModel path)
    {
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            var textBox = FindInlineWorkspaceEditTextBox(topLevel, path, "workspace-path-edit-input");
            if (textBox is not null)
            {
                return textBox;
            }
        }

        return FindInlineWorkspaceEditTextBox(this, path, "workspace-path-edit-input");
    }

    private TextBox? FindInlineWorkspaceDocumentTextBox(AgentWorkspaceDocumentItemViewModel document)
    {
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            var textBox = FindInlineWorkspaceEditTextBox(topLevel, document, "workspace-document-edit-input");
            if (textBox is not null)
            {
                return textBox;
            }
        }

        return FindInlineWorkspaceEditTextBox(this, document, "workspace-document-edit-input");
    }

    private static TextBox? FindInlineWorkspaceEditTextBox(Visual root, object dataContext, string styleClass)
        => root.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(textBox => ReferenceEquals(textBox.DataContext, dataContext)
                                       && textBox.Classes.Contains(styleClass));

    private static void UpdateWorkspaceDrop(DragEventArgs e, bool requireDirectory)
    {
        var paths = GetDroppedStoragePaths(e);
        var canDrop = requireDirectory
            ? paths.Any(Directory.Exists)
            : paths.Any(File.Exists);
        e.DragEffects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static string[] GetDroppedStoragePaths(DragEventArgs e)
        => e.DataTransfer.TryGetFiles()?
            .Select(item => item.Path.LocalPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray()
            ?? [];

    private static void OnSetSelectedEditorPathItemAsDefaultClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AgentEditorPathListFieldViewModel field)
        {
            field.SetSelectedItemAsDefault();
        }
    }

    private static void OnDeleteSelectedEditorPathItemClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AgentEditorPathListFieldViewModel field)
        {
            field.DeleteSelectedItem();
        }
    }

    private void OnPickEditorPathItemSecondaryFolderClick(object? sender, RoutedEventArgs e)
        => _context.Run(cancellationToken => PickEditorPathItemSecondaryFolderAsync(sender, cancellationToken));

    private async Task PickEditorPathItemSecondaryFolderAsync(object? sender, CancellationToken cancellationToken)
    {
        if ((sender as Control)?.DataContext is not AgentEditorPathListItemViewModel item)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select host folder",
            AllowMultiple = false,
        });
        cancellationToken.ThrowIfCancellationRequested();

        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            item.SecondaryValue = folder.Path.LocalPath;
        }
    }

    private void OnEditorActionClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AgentEditorActionViewModel action)
        {
            var viewModel = _viewModel ?? DataContext as AgentWorkspacesViewModel;
            if (viewModel is not null)
            {
                _context.Run(_ => viewModel.ExecuteEditorActionAsync(action));
            }
        }
    }

    private void OnWorkspaceItemTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not AgentWorkspaceRecord workspace)
        {
            return;
        }

        var viewModel = _viewModel ?? DataContext as AgentWorkspacesViewModel;
        viewModel?.ActivateWorkspace(workspace);
        if (viewModel?.IsCompactLayout == true)
        {
            FocusWorkspaceDisplayName();
        }
    }

    private void FocusWorkspaceDisplayName()
    {
        _context.Queue(
            () => WorkspaceDisplayNameTextBox.Focus(),
            DispatcherPriority.Background);
    }

    private void OnContextFailed(Exception exception)
        => (_viewModel ?? DataContext as AgentWorkspacesViewModel)?.ReportPresentationFailure(exception);
}
