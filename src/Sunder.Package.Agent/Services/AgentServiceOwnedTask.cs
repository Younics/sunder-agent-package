namespace Sunder.Package.Agent.Services;

/// <summary>Schedules a new service-owned root, never a nested RPC or permission bypass.</summary>
internal static class AgentServiceOwnedTask
{
    internal static Task Run(Func<Task> work)
    {
        // Durable admission and explicit business inputs establish authority for
        // this root. A request's ambient RPC ancestry/deadline is not its owner.
        // Suppress only while scheduling, never across an await or inside a call.
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(work);
        using var suppression = ExecutionContext.SuppressFlow();
        return Task.Run(work);
    }
}
