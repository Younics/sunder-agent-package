using System.Reflection;
using System.Runtime.Loader;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentOwnerSourceMigrationTests
{
    internal static Task VerifyRoleLocalBehavior(string repository, string root, AssemblyLoadContext load,
        Assembly worker, Assembly app, SunderRpcContractDescriptor descriptor)
    {
        var port = worker.GetType(typeof(IAgentOwnerRuntimeProvider).FullName!, true)!;
        Assert.Same(worker, port.Assembly);
        Assert.NotSame(typeof(IAgentOwnerRuntimeProvider), port);
        Assert.Null(app.GetType(port.FullName!));
        return VerifyBehavior(worker, app, descriptor, probe => AgentRoleBoundary.Convert(probe, port)!);
    }
}
