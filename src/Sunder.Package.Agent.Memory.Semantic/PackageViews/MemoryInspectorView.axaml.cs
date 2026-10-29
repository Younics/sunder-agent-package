using Avalonia.Controls;
using Avalonia;
using Avalonia.Interactivity;
using System.ComponentModel;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic.PackageViews;

[Sunder.Sdk.Packaging.SunderPackageView("younics.ai.agent.memory.semantic.inspector", "Memory Inspector",
    ViewModel = typeof(MemoryInspectorViewModel), IconPath = "assets/memory-icon.png", DefaultPlacement = PackageViewPlacement.LeftBottom)]
public partial class MemoryInspectorView : UserControl, IDisposable, IPackageViewWarmupTarget, IPackageViewNavigationTarget
{
    private MemoryInspectorViewModel? _viewModel;
    private bool _disposed;
    private MemoryInspectorViewModel? _observedModel;
    private bool _detailActive;
    private Guid? _layoutMemoryId;
    private bool? _compact;

    public MemoryInspectorView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyMemoryLayout();
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public MemoryInspectorView(MemoryInspectorViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_observedModel is not null) _observedModel.PropertyChanged -= OnModelChanged;
        _viewModel?.Dispose();
        DataContext = null;
        _viewModel = null;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_observedModel is not null) _observedModel.PropertyChanged -= OnModelChanged;
        base.OnDataContextChanged(e);
        _observedModel = DataContext as MemoryInspectorViewModel;
        if (_observedModel is not null) _observedModel.PropertyChanged += OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MemoryInspectorViewModel.SelectedMemory))
        {
            var selected = _observedModel?.SelectedMemory?.MemoryId;
            if (_observedModel?.IsApplyingMemorySnapshot == true)
            {
                _layoutMemoryId = selected;
                return;
            }
            if (selected == _layoutMemoryId) return;
            _layoutMemoryId = selected;
            _detailActive = selected is not null;
            ApplyMemoryLayout();
        }
    }

    private void ApplyMemoryLayout()
    {
        var compact = Bounds.Width < 820;
        if (compact && _compact == false && MemoryDetailPane.IsKeyboardFocusWithin)
            _detailActive = true;
        if (_compact != compact)
        {
            _compact = compact;
            MemoryDataLayout.ColumnDefinitions = compact ? new ColumnDefinitions("*") : new ColumnDefinitions("320,*");
            Grid.SetColumn(MemoryDetailPane, compact ? 0 : 1);
        }
        MemoryListPane.IsVisible = !compact || !_detailActive;
        MemoryDetailPane.IsVisible = !compact || _detailActive;
        MemoryBackButton.IsVisible = compact;
    }
    private void OnZoomIn(object? sender, RoutedEventArgs e) => MemoryGraph.ZoomBy(1.2);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => MemoryGraph.ZoomBy(1 / 1.2);
    private void OnFitGraph(object? sender, RoutedEventArgs e) => MemoryGraph.Fit();
    private void OnResetGraph(object? sender, RoutedEventArgs e) => MemoryGraph.ResetLayout();
    private void OnOpenData(object? sender, RoutedEventArgs e)
    {
        if (_observedModel is { } model) model.SelectedTab = 1;
        _detailActive = true;
        ApplyMemoryLayout();
    }
    private void OnBackToMemories(object? sender, RoutedEventArgs e)
    {
        _detailActive = false;
        ApplyMemoryLayout();
    }
    private void OnMemoryItemTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        _detailActive = true;
        ApplyMemoryLayout();
    }

    public async ValueTask WarmupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((_viewModel ?? DataContext as MemoryInspectorViewModel) is { } viewModel)
        {
            await viewModel.InitializeAsync(cancellationToken);
        }
    }

    public ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
        => WarmupAsync(cancellationToken);
}
