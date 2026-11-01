extern alias AgentApp;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;
using AppServices = AgentApp::Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Tests;

internal partial class RegressionTestExtensionCatalog
{
    private AgentApp::Sunder.Package.Agent.Services.AgentToolPresentationService? _toolPresentation;

    internal IServiceProviders<AppServices.IWorkspaceEditorService> WorkspaceEditors
        => Receiver("younics.ai.agent").Providers<AppServices.IWorkspaceEditorService>();

    internal AgentApp::Sunder.Package.Agent.Services.AgentToolPresentationService CreateToolPresentationService(IPackageContext context)
    {
        lock (_consumerGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _catalogDisposed) != 0, this);
            if (_toolPresentation is not null) return _toolPresentation;
            var runtime = OwnConsumer("younics.ai.agent", _ => RegressionTestRuntime.Create(context, this));
            var owner = runtime.GetRequiredService<Sunder.Package.Agent.Runtime.AgentOwnerRuntimeProvider>();
            // App presentation goes through its reviewed owner service. Tool-source
            // discovery and invocation stay inside the actual Worker implementation.
            _toolPresentation = new(AddOwnerRuntimeProvider(owner));
            return _toolPresentation;
        }
    }
}

internal static class RegressionTestAppBindings
{
    internal const string WorkspaceEditors = RegressionTestBindings.WorkspaceEditors;
}
