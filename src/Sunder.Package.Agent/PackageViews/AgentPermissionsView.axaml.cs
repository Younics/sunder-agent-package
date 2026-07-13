using Avalonia.Controls;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentPermissionsView : UserControl, IDisposable
{
    private AgentPermissionsViewModel? _viewModel;
    public AgentPermissionsView()
    {
        InitializeComponent();
    }

    public AgentPermissionsView(IAgentPermissionGateway permissionService)
        : this()
    {
        _viewModel = new AgentPermissionsViewModel(permissionService);
        DataContext = _viewModel;
    }

    public void Dispose()
    {
        _viewModel?.Dispose();
        _viewModel = null;
        DataContext = null;
    }
}
