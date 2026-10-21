using Avalonia.Controls;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.Package.Agent.PackageViews;

[Sunder.Sdk.Packaging.SunderPackageSettingsView]
public partial class AgentPermissionsView : UserControl, IDisposable
{
    private AgentPermissionsViewModel? _viewModel;
    private int _disposed;
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

    public AgentPermissionsView(
        IAgentPermissionGateway permissionService,
        IPackageContext packageContext,
        IPackageNotificationService notifications)
        : this()
    {
        _viewModel = new AgentPermissionsViewModel(
            permissionService,
            packageContext,
            notifications);
        DataContext = _viewModel;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _viewModel?.Dispose();
        _viewModel = null;
        DataContext = null;
    }
}
