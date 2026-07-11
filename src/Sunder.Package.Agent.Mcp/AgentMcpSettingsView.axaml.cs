using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using AvaloniaEdit.TextMate;
using Sunder.Package.Agent.Shared.Presentation;
using TextMateSharp.Grammars;

namespace Sunder.Package.Agent.Mcp;

public partial class AgentMcpSettingsView : UserControl, IDisposable
{
    private readonly AdaptiveMasterDetail _adaptiveLayout;
    private AgentMcpSettingsViewModel? _viewModel;
    private AgentMcpSettingsViewModel? _ownedViewModel;
    private bool _syncingEditor;
    private bool _syncingViewModel;
    private bool _disposed;

    public AgentMcpSettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        ConfigEditor.TextChanged += OnEditorTextChanged;
        _adaptiveLayout = new AdaptiveMasterDetail(
            this,
            McpAdaptiveLayout,
            McpListPane,
            McpEditorPane,
            isCompact =>
            {
                var viewModel = _viewModel ?? DataContext as AgentMcpSettingsViewModel;
                if (viewModel is not null)
                {
                    viewModel.IsCompactLayout = isCompact;
                }
            });
        ConfigureEditor();
    }

    public AgentMcpSettingsView(AgentMcpSettingsViewModel viewModel)
        : this()
    {
        _ownedViewModel = viewModel;
        DataContext = viewModel;
    }

    private void ConfigureEditor()
    {
        var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        var textMateInstallation = ConfigEditor.InstallTextMate(registryOptions);
        var language = registryOptions.GetLanguageByExtension(".json");
        if (language is null)
        {
            return;
        }

        textMateInstallation.SetGrammar(registryOptions.GetScopeByLanguageId(language.Id));
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is INotifyPropertyChanged previousViewModel)
        {
            previousViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as AgentMcpSettingsViewModel;
        if (_viewModel is INotifyPropertyChanged currentViewModel)
        {
            currentViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        ApplyViewModelText();
    }

    private void OnAttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _adaptiveLayout.Apply();
        ApplyViewModelText();
        if (McpEditorPane.IsVisible)
        {
            ConfigEditor.Focus();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _adaptiveLayout.Dispose();
        DataContextChanged -= OnDataContextChanged;
        AttachedToVisualTree -= OnAttachedToVisualTree;
        ConfigEditor.TextChanged -= OnEditorTextChanged;
        if (_viewModel is INotifyPropertyChanged viewModel)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        DataContext = null;
        _viewModel = null;
        _ownedViewModel?.Dispose();
        _ownedViewModel = null;
    }

    private void OnServerItemTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ConfiguredMcpServerRecord server)
        {
            return;
        }

        var viewModel = _viewModel ?? DataContext as AgentMcpSettingsViewModel;
        viewModel?.ActivateServer(server);
    }

    private async void OnImportConfigFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var viewModel = _viewModel ?? DataContext as AgentMcpSettingsViewModel;
        if (viewModel is null)
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
            Title = "Select MCP Config File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("MCP config files")
                {
                    Patterns = ["*.json", "*.jsonc"],
                },
            ],
        });

        var file = files.FirstOrDefault();
        if (file is not null)
        {
            await viewModel.ImportConfigurationFileAsync(file.Path.LocalPath);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentMcpSettingsViewModel.EditorText))
        {
            ApplyViewModelText();
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is null || _syncingEditor)
        {
            return;
        }

        var editorText = ConfigEditor.Text ?? string.Empty;
        if (string.Equals(_viewModel.EditorText, editorText, StringComparison.Ordinal))
        {
            return;
        }

        _syncingViewModel = true;
        _viewModel.EditorText = editorText;
        _syncingViewModel = false;
    }

    private void ApplyViewModelText()
    {
        if (_viewModel is null || _syncingViewModel)
        {
            return;
        }

        var editorText = _viewModel.EditorText ?? string.Empty;
        if (string.Equals(ConfigEditor.Text, editorText, StringComparison.Ordinal))
        {
            return;
        }

        _syncingEditor = true;
        ConfigEditor.Text = editorText;
        _syncingEditor = false;
    }
}
