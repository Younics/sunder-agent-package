extern alias AgentApp;

using System.Reflection;
using AgentApp::Sunder.Package.Agent.Contracts.Contracts;
using AppRpc = AgentApp::Sunder.Package.Agent.Protocol;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal partial class RegressionTestExtensionCatalog
{
    internal AppRpc.AgentRpcCatalog AppCatalog => AgentRoleBoundary.As<AppRpc.AgentRpcCatalog>(this);

    public void AddProvider<T>(AppRpc.AgentRpcService<T> service, object implementation, string packageId = "test.package") where T : class
        => AddProvider(service.ContractId, implementation, packageId);

    public AppRpc.AgentRpcReference<T> GetRequiredReference<T>(AppRpc.AgentRpcService<T> service) where T : class
        => Assert.Single(AppCatalog.GetServiceReferences(service));
}

internal static class RegressionTestAppBindings
{
    internal static AppRpc.AgentRpcService<IAgentWorkspaceEditorContributor> WorkspaceEditors { get; } =
        (AppRpc.AgentRpcService<IAgentWorkspaceEditorContributor>)typeof(AppRpc.AgentRpcCatalog).Assembly
            .GetType("Sunder.Package.Agent.Runtime.Rpc.AgentWorkspaceEditorBindings", throwOnError: true)!
            .GetProperty("Service", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
}
