using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentHistorySearchView : UserControl, IDisposable,
    IPackageViewWarmupTarget, IPackageViewNavigationTarget
{
    internal const double MicroLayoutMaximumWidth = 240;
    internal const double MediumLayoutMinimumWidth = 520;
    internal const double WideLayoutMinimumWidth = 820;
    internal const double MaximumContentWidth = 1040;
    private AgentHistorySearchViewModel? _viewModel;
    private bool _disposed;

    public AgentHistorySearchView()
    {
        InitializeComponent();
        SizeChanged += OnViewSizeChanged;
        HistoryScrollViewer.SizeChanged += OnScrollViewerSizeChanged;
        HistoryScrollViewer.LayoutUpdated += OnScrollViewerLayoutUpdated;
        ApplyAdaptiveLayout(Bounds.Width);
    }

    public AgentHistorySearchView(IServiceProvider services)
        : this()
    {
        _viewModel = new AgentHistorySearchViewModel(
            services.GetRequiredService<IAgentHistorySearchGateway>(),
            services.GetRequiredService<AgentChatSelectionStateService>(),
            services.GetRequiredService<IPackageShellViewService>());
        DataContext = _viewModel;
    }

    public async ValueTask WarmupAsync(CancellationToken cancellationToken = default)
    {
        if (ViewModel is not null)
        {
            await InitializeAsync(cancellationToken);
        }
    }

    public async ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InitializeAsync(cancellationToken);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            HistoryQueryTextBox.Focus();
            HistoryQueryTextBox.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        SizeChanged -= OnViewSizeChanged;
        HistoryScrollViewer.SizeChanged -= OnScrollViewerSizeChanged;
        HistoryScrollViewer.LayoutUpdated -= OnScrollViewerLayoutUpdated;
        _viewModel?.Dispose();
        _viewModel = null;
        DataContext = null;
    }

    internal void ApplyAdaptiveLayout(double width)
    {
        var useMicroLayout = width < MicroLayoutMaximumWidth;
        var useCompactLayout = width < MediumLayoutMinimumWidth;
        var useWideLayout = width >= WideLayoutMinimumWidth;
        var useIntermediateLayout = !useCompactLayout && !useWideLayout;
        HistoryLayoutRoot.Classes.Set("micro", useMicroLayout);
        HistoryLayoutRoot.Classes.Set("compact", useCompactLayout);
        HistoryLayoutRoot.Classes.Set("medium", useIntermediateLayout);
        HistoryLayoutRoot.Classes.Set("intermediate", useIntermediateLayout);
        HistoryLayoutRoot.Classes.Set("wide", useWideLayout);
        HistoryContent.Classes.Set("micro", useMicroLayout);
        HistoryContent.Classes.Set("compact", useCompactLayout);
        HistoryContent.Classes.Set("medium", useIntermediateLayout);
        HistoryContent.Classes.Set("intermediate", useIntermediateLayout);
        HistoryContent.Classes.Set("wide", useWideLayout);

        AdvancedSectionsGrid.ColumnDefinitions.Clear();
        if (useCompactLayout)
        {
            AdvancedSectionsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetRow(ScopeSection, 0);
            Grid.SetColumn(ScopeSection, 0);
            Grid.SetRow(ContentSection, 1);
            Grid.SetColumn(ContentSection, 0);
            Grid.SetRow(TimeSection, 2);
            Grid.SetColumn(TimeSection, 0);
            Grid.SetColumnSpan(TimeSection, 1);
        }
        else
        {
            AdvancedSectionsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            AdvancedSectionsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetRow(ScopeSection, 0);
            Grid.SetColumn(ScopeSection, 0);
            Grid.SetRow(ContentSection, 0);
            Grid.SetColumn(ContentSection, 1);
            Grid.SetRow(TimeSection, 1);
            Grid.SetColumn(TimeSection, 0);
            Grid.SetColumnSpan(TimeSection, 2);
        }

        HistoryContent.MaxWidth = MaximumContentWidth;
        HistoryContent.HorizontalAlignment = HorizontalAlignment.Center;
        UpdateHistoryContentWidth(HistoryScrollViewer.Viewport.Width > 0
            ? HistoryScrollViewer.Viewport.Width
            : width);
        if (ViewModel is { } viewModel)
        {
            viewModel.IsCompactLayout = useCompactLayout;
        }
    }

    private AgentHistorySearchViewModel? ViewModel
        => _viewModel ?? DataContext as AgentHistorySearchViewModel;

    private void OnViewSizeChanged(object? sender, SizeChangedEventArgs e)
        => ApplyAdaptiveLayout(e.NewSize.Width);

    private void OnScrollViewerSizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateHistoryContentWidth(HistoryScrollViewer.Viewport.Width > 0
            ? HistoryScrollViewer.Viewport.Width
            : e.NewSize.Width);

    private void OnScrollViewerLayoutUpdated(object? sender, EventArgs e)
        => UpdateHistoryContentWidth(HistoryScrollViewer.Viewport.Width);

    private void UpdateHistoryContentWidth(double viewportWidth)
    {
        if (viewportWidth > 0 && double.IsFinite(viewportWidth))
        {
            var targetWidth = Math.Min(viewportWidth, MaximumContentWidth);
            if (!double.IsFinite(HistoryContent.Width)
                || Math.Abs(HistoryContent.Width - targetWidth) > 0.1)
            {
                HistoryContent.Width = targetWidth;
            }
        }
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }
        var findModifier = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (findModifier && e.Key == Key.F)
        {
            HistoryQueryTextBox.Focus();
            HistoryQueryTextBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            viewModel.EscapeCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && HistoryQueryTextBox.IsKeyboardFocusWithin)
        {
            viewModel.SearchNowCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }
        try
        {
            await viewModel.InitializeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            viewModel.IsInitialLoading = false;
            viewModel.SearchError = "History search could not initialize. Navigate away and return to retry.";
        }
    }
}
