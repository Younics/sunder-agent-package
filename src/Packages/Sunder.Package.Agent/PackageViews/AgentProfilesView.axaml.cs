using Avalonia;
using Avalonia.Controls;
using Sunder.Package.Agent.Services;

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

    public AgentProfilesView(AgentProfileService profileService)
        : this()
    {
        _viewModel = new AgentProfilesViewModel(profileService);
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
}
