using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentProfileObservationTests
{
    [Theory]
    [InlineData(SunderRpcErrorKind.ResourceExhausted, 0)]
    [InlineData(SunderRpcErrorKind.Unavailable, 1)]
    public async Task RepeatedObservationFailureDoesNotAmplifyCatalogRefreshAndRecoveryInvalidatesOnce(SunderRpcErrorKind kind, int failureHints)
    {
        var parent = new AsyncLocal<string?>();
        var chat = new PressureCatalog<AgentServices.IChatProviderService>();
        await using var profiles = new AgentProfileService(null!, null!, chat,
            new Catalog<AgentServices.IBehaviorLoopService>(parent), new Catalog<AgentServices.IEmbeddingProviderService>(parent),
            new Catalog<AgentServices.IProfileCapabilityConsumerService>(parent), new Catalog<AgentServices.ISelectableCapabilityProviderService>(parent),
            new Catalog<AgentServices.IToolSourceService>(parent), new Catalog<AgentServices.IExecutionTargetService>(parent));
        var hints = 0;
        profiles.SelectableCapabilitiesChanged += () => Interlocked.Increment(ref hints);
        await profiles.ListSelectableProfileCapabilitiesAsync();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var request = await chat.NextAsync();
            if (attempt != 0) Assert.Equal(failureHints, Volatile.Read(ref hints));
            request.SetException(new SunderRpcException(new(kind, "test.transient-pressure", "Controlled observation failure.")));
        }
        var recovery = await chat.NextAsync();
        Assert.Equal(failureHints, Volatile.Read(ref hints));
        recovery.SetResult(new(1, 1, []));
        await chat.Watching.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(failureHints + 1, Volatile.Read(ref hints));
    }

    [Fact]
    public async Task SuccessfulSnapshotsBetweenRejectedWatchesDoNotResetTheOutageAndRefreshEveryRetry()
    {
        var parent = new AsyncLocal<string?>();
        var chat = new PressureCatalog<AgentServices.IChatProviderService>(watchFailures: 3);
        await using var profiles = new AgentProfileService(null!, null!, chat,
            new Catalog<AgentServices.IBehaviorLoopService>(parent), new Catalog<AgentServices.IEmbeddingProviderService>(parent),
            new Catalog<AgentServices.IProfileCapabilityConsumerService>(parent), new Catalog<AgentServices.ISelectableCapabilityProviderService>(parent),
            new Catalog<AgentServices.IToolSourceService>(parent), new Catalog<AgentServices.IExecutionTargetService>(parent));
        var hints = 0;
        profiles.SelectableCapabilitiesChanged += () => Interlocked.Increment(ref hints);
        await profiles.ListSelectableProfileCapabilitiesAsync();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var request = await chat.NextAsync();
            Assert.Equal(attempt < 2 ? 0 : 1, Volatile.Read(ref hints));
            request.SetResult(new(1, 1, []));
        }
        await chat.Watching.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref hints));
    }

    [Fact]
    public async Task ActivationObserversDoNotRetainTheFirstRequestsParentAfterCatalogReset()
    {
        var parent = new AsyncLocal<string?> { Value = "first-host-invocation" };
        var chat = new Catalog<AgentServices.IChatProviderService>(parent);
        var loops = new Catalog<AgentServices.IBehaviorLoopService>(parent);
        var embedding = new Catalog<AgentServices.IEmbeddingProviderService>(parent);
        var consumers = new Catalog<AgentServices.IProfileCapabilityConsumerService>(parent);
        var selectable = new Catalog<AgentServices.ISelectableCapabilityProviderService>(parent);
        var tools = new Catalog<AgentServices.IToolSourceService>(parent);
        var execution = new Catalog<AgentServices.IExecutionTargetService>(parent);
        IObservation[] observations = [chat, loops, embedding, consumers, selectable, tools, execution];
        await using var profiles = new AgentProfileService(null!, null!, chat, loops, embedding, consumers, selectable, tools, execution);
        Assert.Empty(await profiles.ListSelectableProfileCapabilitiesAsync());
        await WaitAsync(() => observations.All(item => item.Parents.Count == 1));
        Assert.All(observations, item => Assert.Equal("<activation>", Assert.Single(item.Parents)));
        Assert.Equal("first-host-invocation", parent.Value);
        parent.Value = "later-host-invocation";
        foreach (var item in observations) item.Reset();
        await WaitAsync(() => observations.All(item => item.Parents.Count == 2));
        Assert.All(observations, item => Assert.All(item.Parents, value => Assert.Equal("<activation>", value)));
        Assert.Equal("later-host-invocation", parent.Value);
    }

    private static async Task WaitAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    private interface IObservation
    {
        ConcurrentQueue<string> Parents { get; }
        void Reset();
    }

    private sealed class PressureCatalog<T>(int watchFailures = 0) : IServiceProviders<T> where T : class
    {
        private int _watchFailures = watchFailures;
        private readonly Channel<TaskCompletionSource<ServiceProviderCatalog<T>>> _requests = Channel.CreateUnbounded<TaskCompletionSource<ServiceProviderCatalog<T>>>();
        internal TaskCompletionSource Watching { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<TaskCompletionSource<ServiceProviderCatalog<T>>> NextAsync()
            => _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public async ValueTask<ServiceProviderCatalog<T>> ListAsync(CancellationToken cancellationToken = default)
        {
            var request = new TaskCompletionSource<ServiceProviderCatalog<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _requests.Writer.WriteAsync(request, cancellationToken);
            return await request.Task.WaitAsync(cancellationToken);
        }
        public async IAsyncEnumerable<ServiceProviderCatalogEvent<T>> WatchAsync(long afterRevision, long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _watchFailures) >= 0)
                throw new SunderRpcException(new(SunderRpcErrorKind.ResourceExhausted, "test.watch-pressure", "Controlled Watch rejection."));
            Watching.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class Catalog<T>(AsyncLocal<string?> parent) : IServiceProviders<T>, IObservation where T : class
    {
        private readonly Channel<bool> _reset = Channel.CreateUnbounded<bool>();
        public ConcurrentQueue<string> Parents { get; } = new();
        public void Reset() => _reset.Writer.TryWrite(true);
        public ValueTask<ServiceProviderCatalog<T>> ListAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ServiceProviderCatalog<T>(1, 1, []));
        public async IAsyncEnumerable<ServiceProviderCatalogEvent<T>> WatchAsync(long afterRevision, long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Parents.Enqueue(parent.Value ?? "<activation>");
            await _reset.Reader.ReadAsync(cancellationToken);
            yield return new(2, 2, ServiceProviderChangeKind.ResetRequired, null);
        }
    }
}
