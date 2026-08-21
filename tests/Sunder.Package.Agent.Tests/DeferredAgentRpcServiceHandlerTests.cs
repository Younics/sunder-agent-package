using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class DeferredAgentRpcServiceHandlerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly SunderRpcContractDescriptor TestDescriptor =
        AgentRpcContractDescriptors.Get(AgentRpcContractIds.SessionCleaner);
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> TestDeclaredErrors =
        AgentRpcServiceHandler.CreateDeclaredErrorMap(TestDescriptor);

    [Fact]
    public void DeclaredErrors_AreAvailableBeforeActivationAndMismatchedHandlerIsRejected()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var deferred = new DeferredAgentRpcServiceHandler(
            "test.service",
            TestDescriptor,
            static _ => AgentRpcServiceHandler.Create().Build());

        Assert.Equal(
            FlattenDeclaredErrors(TestDeclaredErrors),
            FlattenDeclaredErrors(deferred.DeclaredErrors));
        var exception = Assert.Throws<InvalidOperationException>(() => deferred.Activate(services));
        Assert.Contains("does not implement the declared domain errors", exception.Message, StringComparison.Ordinal);
        Assert.False(deferred.IsActive);
    }

    private static IReadOnlyList<string> FlattenDeclaredErrors(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> declaredErrors)
        => declaredErrors
            .SelectMany(static service => service.Value.Select(method =>
                $"{service.Key}/{method.Key}:{string.Join(',', method.Value)}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public async Task InvokeUnaryAsync_WaitsForProviderActivation()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var deferred = new DeferredAgentRpcServiceHandler(
            "test.service",
            TestDescriptor,
            static _ => new EchoHandler());
        var request = JsonSerializer.SerializeToElement(new { value = "ready" });

        var invocation = deferred.InvokeUnaryAsync(
            null!,
            "test.service",
            "echo",
            request,
            CancellationToken.None).AsTask();
        await Task.Yield();
        Assert.False(invocation.IsCompleted);

        deferred.Activate(services);

        Assert.Equal(request, await invocation.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Deactivate_CancelsPendingInvocation()
    {
        var deferred = new DeferredAgentRpcServiceHandler(
            "test.service",
            TestDescriptor,
            static _ => new EchoHandler());
        var invocation = deferred.InvokeUnaryAsync(
            null!,
            "test.service",
            "echo",
            default,
            CancellationToken.None).AsTask();

        deferred.Deactivate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invocation.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Deactivate_AfterActivationPreventsUnaryDispatchThroughCompletedActivation()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var handler = new RetirementAwareHandler();
        var deferred = new DeferredAgentRpcServiceHandler("test.service", TestDescriptor, _ => handler);
        deferred.Activate(services);

        deferred.Deactivate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deferred.InvokeUnaryAsync(
            null!,
            "test.service",
            "blocked",
            default).AsTask());
        Assert.Equal(0, handler.UnaryInvocations);
    }

    [Fact]
    public async Task Deactivate_AfterActivationPreventsStreamDispatchThroughCompletedActivation()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var handler = new RetirementAwareHandler();
        var deferred = new DeferredAgentRpcServiceHandler("test.service", TestDescriptor, _ => handler);
        deferred.Activate(services);

        deferred.Deactivate();

        await using var stream = deferred.InvokeServerStreamAsync(
                null!,
                "test.service",
                "blocked",
                default)
            .GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stream.MoveNextAsync().AsTask());
        Assert.Equal(0, handler.StreamInvocations);
    }

    [Fact]
    public async Task Deactivate_CancelsActiveUnaryInvocation()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var handler = new RetirementAwareHandler();
        var deferred = new DeferredAgentRpcServiceHandler("test.service", TestDescriptor, _ => handler);
        deferred.Activate(services);
        var invocation = deferred.InvokeUnaryAsync(
            null!,
            "test.service",
            "blocked",
            default).AsTask();
        await handler.UnaryStarted.Task.WaitAsync(TestTimeout);

        deferred.Deactivate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invocation.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Deactivate_CancelsActiveStreamInvocation()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var handler = new RetirementAwareHandler();
        var deferred = new DeferredAgentRpcServiceHandler("test.service", TestDescriptor, _ => handler);
        deferred.Activate(services);
        await using var stream = deferred.InvokeServerStreamAsync(
                null!,
                "test.service",
                "blocked",
                default)
            .GetAsyncEnumerator();
        var moveNext = stream.MoveNextAsync().AsTask();
        await handler.StreamStarted.Task.WaitAsync(TestTimeout);

        deferred.Deactivate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => moveNext.WaitAsync(TestTimeout));
    }

    private sealed class EchoHandler : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
    {
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors
            => TestDeclaredErrors;

        public ValueTask<JsonElement> InvokeUnaryAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(request);
        }

        public async IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return request;
            await Task.CompletedTask;
        }
    }

    private sealed class RetirementAwareHandler : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
    {
        private int _streamInvocations;
        private int _unaryInvocations;

        internal int StreamInvocations => Volatile.Read(ref _streamInvocations);

        internal int UnaryInvocations => Volatile.Read(ref _unaryInvocations);

        internal TaskCompletionSource StreamStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource UnaryStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors
            => TestDeclaredErrors;

        public async ValueTask<JsonElement> InvokeUnaryAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _unaryInvocations);
            UnaryStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return request;
        }

        public async IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _streamInvocations);
            StreamStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield return request;
        }
    }
}
