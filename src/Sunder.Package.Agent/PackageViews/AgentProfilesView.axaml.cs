using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentProfilesView : UserControl, IDisposable, IPackageViewWarmupTarget, IPackageViewNavigationTarget
{
    private readonly AdaptiveMasterDetail _adaptiveLayout;
    private readonly PresentationTaskScope _tasks = new();
    private AgentProfilesViewModel? _viewModel;
    private bool _disposed;

    public AgentProfilesView()
    {
        InitializeComponent();
        _adaptiveLayout = new AdaptiveMasterDetail(
            this,
            ProfileAdaptiveLayout,
            ProfileListPane,
            ProfileEditorPane,
            isCompact =>
            {
                var viewModel = _viewModel ?? DataContext as AgentProfilesViewModel;
                if (viewModel is not null)
                {
                    viewModel.IsCompactLayout = isCompact;
                }
            });
    }

    public AgentProfilesView(
        IAgentProfileGateway profileService,
        IPackageSettingsNavigationService? settingsNavigationService = null)
        : this()
    {
        _viewModel = new AgentProfilesViewModel(profileService, settingsNavigationService);
        DataContext = _viewModel;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _adaptiveLayout.Dispose();
        _tasks.Dispose();
        _viewModel?.Dispose();
        DataContext = null;
        _viewModel = null;
    }

    public async ValueTask WarmupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((_viewModel ?? DataContext as AgentProfilesViewModel) is { } viewModel)
        {
            await viewModel.InitializeAsync(cancellationToken);
        }
    }

    public ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
        => WarmupAsync(cancellationToken);

    private void OnProfileItemTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not AgentProfileRecord profile)
        {
            return;
        }

        var viewModel = _viewModel ?? DataContext as AgentProfilesViewModel;
        viewModel?.ActivateProfile(profile);
        if (viewModel?.IsCompactLayout == true)
        {
            FocusProfileDisplayName();
        }
    }

    private void FocusProfileDisplayName()
    {
        _tasks.Run(async cancellationToken =>
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    ProfileDisplayNameTextBox.Focus();
                }
            }, DispatcherPriority.Background);
        });
    }
}
