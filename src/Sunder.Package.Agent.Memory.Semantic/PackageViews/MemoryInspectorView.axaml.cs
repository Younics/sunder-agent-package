using Avalonia.Controls;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic.PackageViews;

public partial class MemoryInspectorView : UserControl, IDisposable, IPackageViewWarmupTarget, IPackageViewNavigationTarget
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
