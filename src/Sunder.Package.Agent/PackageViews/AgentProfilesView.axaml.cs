using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentProfilesView : UserControl
{
    private const double WideProfileMinimumWidth = 820;

    private AgentProfilesViewModel? _viewModel;

    public AgentProfilesView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyResponsiveLayout();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    public AgentProfilesView(
        AgentProfileService profileService,
        IPackageSettingsNavigationService? settingsNavigationService = null)
        : this()
    {
        _viewModel = new AgentProfilesViewModel(profileService, settingsNavigationService);
        DataContext = _viewModel;
    }

    private void ApplyResponsiveLayout()
    {
        var useCompactLayout = Bounds.Width > 0 && Bounds.Width < WideProfileMinimumWidth;
        var viewModel = _viewModel ?? DataContext as AgentProfilesViewModel;
        if (viewModel is not null)
        {
            viewModel.IsCompactLayout = useCompactLayout;
        }

        ProfileAdaptiveLayout.ColumnSpacing = useCompactLayout ? 0 : 4;
        ProfileListPane.BorderThickness = useCompactLayout ? new Thickness(0) : new Thickness(0, 0, 1, 0);

        Grid.SetColumn(ProfileListPane, 0);
        Grid.SetColumn(ProfileEditorPane, useCompactLayout ? 0 : 1);
        Grid.SetColumnSpan(ProfileListPane, useCompactLayout ? 2 : 1);
        Grid.SetColumnSpan(ProfileEditorPane, useCompactLayout ? 2 : 1);
    }

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
        Dispatcher.UIThread.Post(
            () => ProfileDisplayNameTextBox.Focus(),
            DispatcherPriority.Background);
    }
}
