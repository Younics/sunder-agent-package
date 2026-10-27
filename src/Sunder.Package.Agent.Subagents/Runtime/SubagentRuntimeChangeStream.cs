using System.Runtime.CompilerServices;
using System.Threading.Channels;
using IRuntimeCatalogService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Subagents.Runtime;

internal sealed class SubagentRuntimeChangeStream : IDisposable, IAsyncDisposable
{
    private const int ReplayCapacity = 256;
    private const int SubscriberCapacity = 64;
    private readonly SubagentService _service;
    private readonly SubagentEditorCapabilityCatalog _capabilities;
    private readonly IServiceProviders<IRuntimeCatalogService> _catalogs;
    private readonly object _gate = new();
    private readonly Dictionary<long, Subscriber> _subscribers = [];
    private readonly Queue<SubagentChanged> _replay = new(ReplayCapacity);
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _lifetime = new();
    private Task _watch = Task.CompletedTask;
    private Task? _stop;
    private long _revision;
    private long _subscriberId;
    private bool _started;
    private bool _disposed;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal SubagentRuntimeChangeStream(SubagentService service, SubagentEditorCapabilityCatalog capabilities, IServiceProviders<IRuntimeCatalogService> catalogs)
    {
        _service = service; _capabilities = capabilities; _catalogs = catalogs;
        service.SubagentsChanged += OnSubagentsChanged;
        capabilities.Changed += OnCatalogChanged;
    }
    internal long Revision => Interlocked.Read(ref _revision);
    internal ValueTask StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started) { _started = true; _watch = Task.Run(WatchRuntimeAsync); }
        }
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<SubagentChanged> SubscribeAsync(long afterRevision, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscriber = new Subscriber(Channel.CreateBounded<SubagentChanged>(new BoundedChannelOptions(SubscriberCapacity)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait }));
        SubagentChanged[] replay;
        SubagentChanged? reset = null;
        long subscribedRevision;
        var id = Interlocked.Increment(ref _subscriberId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var revision = Revision;
            subscribedRevision = revision;
            var oldest = _replay.Count == 0 ? revision + 1 : _replay.Peek().Revision;
            if (afterRevision > revision || afterRevision < oldest - 1) { replay = []; reset = CreateResnapshot(revision); }
            else replay = _replay.Where(change => change.Revision > afterRevision).ToArray();
            _subscribers[id] = subscriber;
        }
        try
        {
            if (reset is not null) yield return reset;
            else
            {
                foreach (var change in replay) yield return change;
                yield return new(subscribedRevision, SubagentChangeKind.Connected, RuntimeInstanceId: _instanceId);
            }
            await foreach (var change in subscriber.Channel.Reader.ReadAllAsync(cancellationToken)) yield return change;
            if (subscriber.Overflowed) yield return CreateResnapshot(Revision);
        }
        finally { lock (_gate) _subscribers.Remove(id); subscriber.Channel.Writer.TryComplete(); }
    }

    private async Task WatchRuntimeAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var snapshot = await _catalogs.ListAsync(_lifetime.Token).ConfigureAwait(false);
                var provider = snapshot.Providers.FirstOrDefault(item => item.State == ServiceProviderState.Active);
                if (provider is null)
                {
                    await foreach (var _ in _catalogs.WatchAsync(snapshot.Revision, snapshot.Sequence, _lifetime.Token).ConfigureAwait(false)) break;
                    continue;
                }
                using var invocation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, provider.RetirementToken);
                await provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
                PublishRuntimeCatalogChanged();
                long expected = 1;
                await foreach (var change in provider.Service.WatchAsync(invocation.Token).ConfigureAwait(false))
                {
                    invocation.Token.ThrowIfCancellationRequested();
                    var sequence = change switch
                    {
                        AgentRuntimeCatalogResetEvent value => value.Sequence,
                        AgentSessionCatalogEvent value => value.Sequence,
                        AgentTurnCatalogEvent value => value.Sequence,
                        AgentProfileCatalogEvent value => value.Sequence,
                        AgentRunActivityCatalogEvent value => value.Sequence,
                        _ => throw new InvalidDataException("Unknown Runtime catalog event."),
                    };
                    if (sequence != expected && change is not AgentRuntimeCatalogResetEvent) Publish(new(0, SubagentChangeKind.ResnapshotRequired));
                    expected = sequence + 1;
                    switch (change)
                    {
                        case AgentRuntimeCatalogResetEvent: Publish(new(0, SubagentChangeKind.ResnapshotRequired)); break;
                        case AgentSessionCatalogEvent session: Publish(new(0, SubagentChangeKind.Session, SessionId: session.SessionId)); break;
                        case AgentTurnCatalogEvent turn: Publish(new(0, SubagentChangeKind.Turn, SessionId: turn.SessionId, Turn: turn.Turn, ActivityGroup: turn.ActivityGroup)); break;
                        case AgentProfileCatalogEvent profile: Publish(new(0, SubagentChangeKind.Profile, ProfileId: profile.ProfileId)); break;
                        case AgentRunActivityCatalogEvent activity: Publish(new(0, SubagentChangeKind.Activity, SessionId: activity.SessionId, Activity: activity.Activity)); break;
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (error is SunderRpcException or OperationCanceledException) { /* Resnapshot on metadata/stream recovery. */ }
            PublishRuntimeCatalogChanged();
            try { await Task.Delay(250, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        }
    }

    private void PublishRuntimeCatalogChanged()
    {
        // Runtime availability changes invalidate both data and catalog views. The
        // independent capability watcher may already have emitted its Catalog event.
        lock (_gate)
        {
            Publish(new(0, SubagentChangeKind.ResnapshotRequired));
            Publish(new(0, SubagentChangeKind.Catalog));
        }
    }

    private void OnSubagentsChanged() => Publish(new(0, SubagentChangeKind.Subagents));
    private void OnCatalogChanged() => Publish(new(0, SubagentChangeKind.Catalog));
    private void Publish(SubagentChanged change)
    {
        lock (_gate)
        {
            if (_disposed) return;
            change = SubsessionAroundTurnPayload.FitChange(change with { Revision = Interlocked.Increment(ref _revision), RuntimeInstanceId = _instanceId });
            _replay.Enqueue(change);
            while (_replay.Count > ReplayCapacity) _replay.Dequeue();
            foreach (var subscriber in _subscribers.Values)
                if (!subscriber.Channel.Writer.TryWrite(change)) { subscriber.Overflowed = true; subscriber.Channel.Writer.TryComplete(); }
        }
    }
    private SubagentChanged CreateResnapshot(long revision) => new(revision, SubagentChangeKind.ResnapshotRequired, RuntimeInstanceId: _instanceId);
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var subscriber in _subscribers.Values) subscriber.Channel.Writer.TryComplete();
            _subscribers.Clear();
        }
        _service.SubagentsChanged -= OnSubagentsChanged;
        _capabilities.Changed -= OnCatalogChanged;
        _lifetime.Cancel();
    }
    public ValueTask DisposeAsync() { Dispose(); lock (_gate) return new(_stop ??= StopAsync()); }
    private async Task StopAsync() { try { await _watch.ConfigureAwait(false); } finally { _lifetime.Dispose(); } }
    private sealed class Subscriber(Channel<SubagentChanged> channel)
    {
        internal Channel<SubagentChanged> Channel { get; } = channel;
        internal bool Overflowed { get; set; }
    }
}
