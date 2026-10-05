using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static JsonElement ExecutionWireContext(AgentExecutionTargetContext context)
        => RegressionTestRpcValues.ExecutionContext(context);

    private static AgentResolvedResource ReadExecutionResource(JsonElement value)
        => Runtime.Rpc.AgentExecutionValues.Resource(value.Deserialize<Sunder.Generated.Rpc.ExecutionTarget.AgentResolvedResourceWire>(MemoryProviderJson)!);

    private static AgentFileMutationResult ReadExecutionMutation(JsonElement value)
        => Runtime.Rpc.AgentExecutionValues.Mutation(value.Deserialize<Sunder.Generated.Rpc.ExecutionTarget.AgentFileMutationResultWire>(MemoryProviderJson)!);

    private static object ToolSourceContext(AgentToolSourceContext context, AgentRpcProviderHandle? target = null, string? generation = null)
        => new { Context = context, ExecutionTarget = target, ExecutionTargetConfigurationGeneration = generation };

    private static object ToolReadiness(string toolId, AgentToolSourceContext context, AgentRpcProviderHandle? target = null, string? generation = null)
        => new { ToolId = toolId, Context = ToolSourceContext(context, target, generation) };

    private static AgentPermissionRequest ReadToolPermission(JsonElement value)
    {
        var wire = value.Deserialize<Sunder.Generated.Rpc.ToolSource.AgentPermissionRequestWire>(MemoryProviderJson)!;
        return Runtime.Rpc.AgentConsumerValues.Convert<AgentPermissionRequest>(wire.Request)
            with { ResourceCapabilities = wire.ResourceCapabilities.ToArray() };
    }

    // Test-only value boundary: keep the real role instance/lease, while assertions
    // consume serialized snapshots rather than casting across CLR role identities.
    private sealed class RoleLocalValue(object value) : IDisposable
    {
        private readonly List<(EventInfo Event, Delegate Handler)> _events = [];
        internal object Value => value;
        internal T Property<T>(string name)
        {
            try { return Project<T>(value.GetType().GetProperty(name)!.GetValue(value)); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        internal T Invoke<T>(string name, params object?[] arguments)
        {
            var (method, local) = Prepare(name, arguments);
            try { return Project<T>(method.Invoke(value, local)); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        internal async ValueTask<T> InvokeAsync<T>(string name, params object?[] arguments)
        {
            var (method, local) = Prepare(name, arguments);
            object pending;
            try { pending = method.Invoke(value, local)!; }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
            var task = pending as Task ?? (Task)pending.GetType().GetMethod("AsTask")!.Invoke(pending, null)!;
            await task.WaitAsync(TimeSpan.FromSeconds(20));
            return Project<T>(task.GetType().GetProperty("Result")?.GetValue(task));
        }
        internal void On(string name, Delegate handler)
        {
            var member = value.GetType().GetEvent(name)!;
            member.AddEventHandler(value, handler); _events.Add((member, handler));
        }
        internal void OnTurn(Action<Guid, AgentTurnRecord> callback)
        {
            var turnType = value.GetType().GetEvent("TurnChanged")!.EventHandlerType!.GetGenericArguments()[1];
            Action<Guid, object> project = (id, turn) => callback(id, Project<AgentTurnRecord>(turn));
            var handler = (Delegate)typeof(RoleLocalValue).GetMethod(nameof(TurnHandler), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(turnType).Invoke(null, [project])!;
            On("TurnChanged", handler);
        }
        private static Delegate TurnHandler<T>(Action<Guid, object> callback) => new Action<Guid, T>((id, turn) => callback(id, turn!));
        private (MethodInfo Method, object?[] Arguments) Prepare(string name, object?[] arguments)
        {
            var method = value.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
            var parameters = method.GetParameters();
            return (method, arguments.Select((argument, index) => argument is null || parameters[index].ParameterType.IsInstanceOfType(argument)
                ? argument : JsonSerializer.SerializeToElement(argument, MemoryProviderJson).Deserialize(parameters[index].ParameterType, MemoryProviderJson)).ToArray());
        }
        private static T Project<T>(object? item) => item is T result ? result
            : JsonSerializer.SerializeToElement(item, MemoryProviderJson).Deserialize<T>(MemoryProviderJson)!;
        public void Dispose()
        {
            foreach (var (member, handler) in _events) member.RemoveEventHandler(value, handler);
            _events.Clear();
        }
    }

    private sealed class SubagentRunControlProbe(object client)
    {
        private readonly RoleLocalValue _value = new(client);
        internal ValueTask<AgentBehaviorLoopResult> RunDefaultLoopAsync(AgentBehaviorLoopContext context, CancellationToken token = default)
            => _value.InvokeAsync<AgentBehaviorLoopResult>("RunDefaultLoopAsync", context, token);
    }
}
