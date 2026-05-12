using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit.TextMate;
using Sunder.Package.Agent.Mcp.Services;
using TextMateSharp.Grammars;

namespace Sunder.Package.Agent.Mcp;

public partial class AgentMcpSettingsView : UserControl
{
    private const double WideMcpMinimumWidth = 820;

    private AgentMcpSettingsViewModel? _viewModel;
    private bool _syncingEditor;
    private bool _syncingViewModel;

    public AgentMcpSettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        Loaded += (_, _) => ApplyResponsiveLayout();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
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
        ApplyResponsiveLayout();
        ApplyViewModelText();
        if (McpEditorPane.IsVisible)
        {
            ConfigEditor.Focus();
        }
    }

    private void ApplyResponsiveLayout()
    {
        var useCompactLayout = Bounds.Width > 0 && Bounds.Width < WideMcpMinimumWidth;
        var viewModel = _viewModel ?? DataContext as AgentMcpSettingsViewModel;
        if (viewModel is not null)
        {
            viewModel.IsCompactLayout = useCompactLayout;
        }

        McpAdaptiveLayout.ColumnSpacing = useCompactLayout ? 0 : 4;
        McpListPane.BorderThickness = useCompactLayout ? new Thickness(0) : new Thickness(0, 0, 1, 0);

        Grid.SetColumn(McpListPane, 0);
        Grid.SetColumn(McpEditorPane, useCompactLayout ? 0 : 1);
        Grid.SetColumnSpan(McpListPane, useCompactLayout ? 2 : 1);
        Grid.SetColumnSpan(McpEditorPane, useCompactLayout ? 2 : 1);
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
