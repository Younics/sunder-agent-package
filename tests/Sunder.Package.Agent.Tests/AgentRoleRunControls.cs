using System.Reflection;
using Sunder.Package.Agent.Contracts.Contracts;

namespace Sunder.Package.Agent.Tests;

internal sealed class AgentRoleRunControls(Assembly role)
{
    internal object Value { get; } = Activator.CreateInstance(role.GetType("Sunder.Package.Agent.Protocol.AgentRunControlRegistry", true)!)!;

    internal IDisposable Open(IAgentBehaviorLoopRuntime runtime, string caller, out Guid invocation)
    {
        var method = Value.GetType().GetMethod("Open")!;
        object?[] arguments = [AgentRoleBoundary.Convert(runtime, method.GetParameters()[0].ParameterType), caller, null];
        var lease = (IDisposable)method.Invoke(Value, arguments)!;
        invocation = (Guid)arguments[2]!;
        return lease;
    }
}
