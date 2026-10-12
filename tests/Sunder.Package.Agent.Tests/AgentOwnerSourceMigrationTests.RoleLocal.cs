using System.Reflection;
using System.Runtime.Loader;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Rpc;
using Xunit;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentOwnerSourceMigrationTests
{
    internal static Task VerifyRoleLocalBehavior(string repository, string root, AssemblyLoadContext load,
        Assembly worker, Assembly app, SunderRpcContractDescriptor descriptor)
    {
        var port = worker.GetType(typeof(AgentServices.IAgentRuntimeService).FullName!, true)!;
        Assert.Same(worker, port.Assembly);
        Assert.NotSame(typeof(AgentServices.IAgentRuntimeService), port);
        var appPort = app.GetType(port.FullName!, true)!;
        Assert.Same(app, appPort.Assembly);
        Assert.NotSame(port, appPort);
        return VerifyBehavior(worker, app, descriptor);
    }
}
