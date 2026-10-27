using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.AI;

namespace Sunder.Package.Agent.Tests;

// Role-local domain failures are values too. The fixture must not accidentally
// turn a normalized provider error into an unrecognized CLR exception when its
// controlled collaborator is invoked from another role. SDK/cancellation/fatal
// exceptions retain their original identity and cancellation token.
internal static class AgentRoleExceptionBoundary
{
    internal static Exception Project(Exception error, Assembly role)
    {
        if (error.GetType().Assembly == role) return error;
        if (error is AggregateException aggregate)
        {
            var projectedErrors = aggregate.InnerExceptions.Select(inner => Project(inner, role)).ToArray();
            return projectedErrors.Where((inner, index) => !ReferenceEquals(inner, aggregate.InnerExceptions[index])).Any()
                ? new AggregateException(aggregate.Message, projectedErrors) : error;
        }
        var name = error.GetType().FullName;
        if (name is not ("Sunder.Package.Agent.Contracts.Models.AgentChatProviderException"
            or "Sunder.Package.Agent.Contracts.Contracts.AgentDurableLifecycleIntegrityException"
            or "Sunder.Package.Agent.Runtime.AgentOwnerOperationException"
            or "Sunder.Package.Agent.Memory.Semantic.Runtime.MemoryInspectorOperationException"
            or "Sunder.Package.Agent.Mcp.Services.McpManagementOperationException")) return error;
        var target = role.GetType(name);
        if (target is null) return error;
        if (name.EndsWith("AgentDurableLifecycleIntegrityException", StringComparison.Ordinal))
            return (Exception)Activator.CreateInstance(target, error.Message)!;
        if (name.EndsWith("OperationException", StringComparison.Ordinal))
            return (Exception)Activator.CreateInstance(target, error.Message,
                error.InnerException is { } operationInner ? Project(operationInner, role) : null)!;
        var source = error.GetType();
        var projected = (Exception)Activator.CreateInstance(target, error.Message,
            source.GetProperty("Content")!.GetValue(error), source.GetProperty("ErrorCode")!.GetValue(error),
            error.InnerException is { } providerInner ? Project(providerInner, role) : null)!;
        var failure = target.GetProperty("FailureKind")!;
        failure.SetValue(projected, AgentRoleBoundary.Convert(source.GetProperty("FailureKind")!.GetValue(error), failure.PropertyType));
        foreach (var nameOfProperty in new[] { "StatusCode", "RetryAfterMilliseconds" })
        {
            var property = target.GetProperty(nameOfProperty)!;
            property.SetValue(projected, source.GetProperty(nameOfProperty)!.GetValue(error));
        }
        return projected;
    }

    internal static object? Return(object? value, Type target, Assembly role)
    {
        if (value is null) return null;
        if (target.IsGenericType && target.GetGenericTypeDefinition() is var kind
            && (kind == typeof(ValueTask<>) || kind == typeof(Task<>) || kind == typeof(IAsyncEnumerable<>)))
        {
            var method = kind == typeof(ValueTask<>) ? nameof(Value) : kind == typeof(Task<>) ? nameof(Result) : nameof(Stream);
            return typeof(AgentRoleExceptionBoundary).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(target.GenericTypeArguments[0]).Invoke(null, method == nameof(Stream) ? [value, role, CancellationToken.None] : [value, role]);
        }
        if (target == typeof(ValueTask)) return new ValueTask(Completion(value, role));
        if (target == typeof(Task)) return Completion(value, role);
        return AgentRoleBoundary.Convert(value, target);
    }

    private static async Task Completion(object pending, Assembly role)
    {
        try { await AgentRoleBoundary.Await(pending).ConfigureAwait(false); }
        catch (Exception error) { ExceptionDispatchInfo.Capture(Project(error, role)).Throw(); throw; }
    }
    private static async Task<T> Result<T>(object pending, Assembly role)
    {
        try
        {
            var value = await AgentRoleBoundary.Await(pending).ConfigureAwait(false);
            if (typeof(T) == typeof(IChatClient) && value is IChatClient client) return (T)(object)new RoleChatClient(client, role);
            return AgentRoleBoundary.As<T>(value!);
        }
        catch (Exception error) { ExceptionDispatchInfo.Capture(Project(error, role)).Throw(); throw; }
    }
    private static async ValueTask<T> Value<T>(object pending, Assembly role) => await Result<T>(pending, role).ConfigureAwait(false);
    private static async IAsyncEnumerable<T> Stream<T>(object value, Assembly role, [EnumeratorCancellation] CancellationToken token = default)
    {
        await using var iterator = AgentRoleBoundary.As<IAsyncEnumerable<T>>(value).GetAsyncEnumerator(token);
        while (await Next(iterator, role).ConfigureAwait(false)) yield return iterator.Current;
    }
    private static async ValueTask<bool> Next<T>(IAsyncEnumerator<T> iterator, Assembly role)
    {
        try { return await iterator.MoveNextAsync().ConfigureAwait(false); }
        catch (Exception error) { ExceptionDispatchInfo.Capture(Project(error, role)).Throw(); throw; }
    }

    private sealed class RoleChatClient(IChatClient inner, Assembly role) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            try { return Result<ChatResponse>(inner.GetResponseAsync(messages, options, cancellationToken), role); }
            catch (Exception error) { ExceptionDispatchInfo.Capture(Project(error, role)).Throw(); throw; }
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            try { return Stream<ChatResponseUpdate>(inner.GetStreamingResponseAsync(messages, options, cancellationToken), role, cancellationToken); }
            catch (Exception error) { ExceptionDispatchInfo.Capture(Project(error, role)).Throw(); throw; }
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);
        public void Dispose() => inner.Dispose();
    }
}
