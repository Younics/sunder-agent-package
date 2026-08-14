using Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.Tests;

internal static class AgentOwnerOperationTestExtensions
{
    public static bool Is<TRequest, TResponse, TExpectedRequest, TExpectedResponse>(
        this AgentOwnerRpcOperation<TRequest, TResponse> operation,
        AgentOwnerRpcOperation<TExpectedRequest, TExpectedResponse> expected)
        where TRequest : class
        where TResponse : class
        where TExpectedRequest : class
        where TExpectedResponse : class
        => ReferenceEquals(operation, expected);

    public static bool Is<TRequest, TEvent, TExpectedRequest, TExpectedEvent>(
        this AgentOwnerRpcStream<TRequest, TEvent> stream,
        AgentOwnerRpcStream<TExpectedRequest, TExpectedEvent> expected)
        where TRequest : class
        where TEvent : class
        where TExpectedRequest : class
        where TExpectedEvent : class
        => ReferenceEquals(stream, expected);

    public static string Describe<TRequest, TResponse>(
        this AgentOwnerRpcOperation<TRequest, TResponse> operation)
        where TRequest : class
        where TResponse : class
        => $"{typeof(TRequest).Name} -> {typeof(TResponse).Name}";

    public static string Describe<TRequest, TEvent>(
        this AgentOwnerRpcStream<TRequest, TEvent> stream)
        where TRequest : class
        where TEvent : class
        => $"{typeof(TRequest).Name} -> stream<{typeof(TEvent).Name}>";
}
