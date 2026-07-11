using Avalonia.Controls;

namespace Sunder.Package.Agent.Memory.Semantic.PackageViews;

public partial class MemoryInspectorView : UserControl, IDisposable
{
    private MemoryInspectorViewModel? _viewModel;
    private bool _disposed;

    public MemoryInspectorView()
    {
        InitializeComponent();
    }

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
        _viewModel?.Dispose();
        DataContext = null;
        _viewModel = null;
    }
}
