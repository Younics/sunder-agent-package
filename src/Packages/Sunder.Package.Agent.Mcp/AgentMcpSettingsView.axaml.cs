using System.ComponentModel;
using Avalonia.Controls;
using AvaloniaEdit.TextMate;
using Sunder.Package.Agent.Mcp.Services;
using TextMateSharp.Grammars;

namespace Sunder.Package.Agent.Mcp;

public partial class AgentMcpSettingsView : UserControl
{
    private AgentMcpSettingsViewModel? _viewModel;
    private bool _syncingEditor;
    private bool _syncingViewModel;

    public AgentMcpSettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        ConfigEditor.TextChanged += OnEditorTextChanged;
        ConfigureEditor();
    }

    public AgentMcpSettingsView(
        McpServerCatalogService serverCatalogService,
        McpClientConnectionManager connectionManager)
        : this()
    {
        DataContext = new AgentMcpSettingsViewModel(serverCatalogService, connectionManager);
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
        ApplyViewModelText();
        ConfigEditor.Focus();
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
