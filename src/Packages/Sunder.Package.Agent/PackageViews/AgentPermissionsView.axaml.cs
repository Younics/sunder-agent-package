using Avalonia.Controls;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentPermissionsView : UserControl
{
    public AgentPermissionsView()
    {
        InitializeComponent();
    }

    public AgentPermissionsView(AgentPermissionService permissionService)
        : this()
    {
        DataContext = new AgentPermissionsViewModel(permissionService);
    }
}
