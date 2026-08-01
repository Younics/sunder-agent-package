using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Subagents.Runtime;

internal sealed class SubagentRuntimeChangeStream :
    IPackageRuntimeStreamHandler<SubagentChangeSubscription, SubagentChanged>,
    IDisposable
{
    private const int ReplayCapacity = 256;
    private const int SubscriberCapacity = 64;
    private readonly SubagentService _service;
    private readonly SubagentEditorCapabilityCatalog _capabilities;
    private readonly AgentRpcCatalog _rpcCatalog;
    private readonly object _gate = new();
    private readonly object _runtimeGate = new();
    private readonly Dictionary<long, Subscriber> _subscribers = [];
    private readonly Queue<SubagentChanged> _replay = new(ReplayCapacity);
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private AgentRpcReference<IAgentRuntimeCatalog>? _runtimeReference;
    private AgentRpcLease<IAgentRuntimeCatalog>? _runtimeLease;
    private IAgentRuntimeCatalog? _runtime;
    private CancellationTokenRegistration _runtimeRetirement;
    private long _runtimeGeneration;
    private long _revision;
    private long _subscriberId;
    private bool _disposed;

    public SubagentRuntimeChangeStream(
        SubagentService service,
        AgentRpcCatalog rpcCatalog)
    {
        _service = service;
        _rpcCatalog = rpcCatalog;
        _capabilities = new SubagentEditorCapabilityCatalog(rpcCatalog);
        service.SubagentsChanged += OnSubagentsChanged;
        _capabilities.Changed += OnCatalogChanged;
        RefreshRuntime();
    }

    internal long Revision => Interlocked.Read(ref _revision);

    public async IAsyncEnumerable<SubagentChanged> SubscribeAsync(
        SubagentChangeSubscription request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscriber = new Subscriber(
            Channel.CreateBounded<SubagentChanged>(new BoundedChannelOptions(SubscriberCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            }));
        SubagentChanged[] replay;
        SubagentChanged? reset = null;
        long subscribedRevision;
        var id = Interlocked.Increment(ref _subscriberId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var revision = Revision;
            subscribedRevision = revision;
            var oldestRevision = _replay.Count == 0 ? revision + 1 : _replay.Peek().Revision;
            if (request.AfterRevision > revision || request.AfterRevision < oldestRevision - 1)
            {
                replay = [];
                reset = CreateResnapshot(revision);
            }
            else
            {
                replay = _replay.Where(change => change.Revision > request.AfterRevision).ToArray();
            }
            _subscribers[id] = subscriber;
        }

        try
        {
            if (reset is not null)
            {
                yield return reset;
            }
            else
            {
                foreach (var change in replay)
                {
                    yield return change;
                }
                yield return new SubagentChanged(
                    subscribedRevision,
                    SubagentChangeKind.Connected,
                    RuntimeInstanceId: _instanceId);
            }

            await foreach (var change in subscriber.Channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return change;
            }
            if (subscriber.Overflowed)
            {
                yield return CreateResnapshot(Revision);
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(id);
            }
            subscriber.Channel.Writer.TryComplete();
        }
    }

    private void OnSubagentsChanged() => Publish(new(0, SubagentChangeKind.Subagents));

    private void OnCatalogChanged()
    {
        var runtimeChanged = false;
        try
        {
            runtimeChanged = RefreshRuntime();
        }
        finally
        {
            if (runtimeChanged) Publish(new(0, SubagentChangeKind.ResnapshotRequired));
            Publish(new(0, SubagentChangeKind.Catalog));
        }
    }

    private void OnSessionChanged(Guid sessionId)
        => Publish(new(0, SubagentChangeKind.Session, SessionId: sessionId));

    private void OnTurnChanged(Guid sessionId, AgentTurnRecord turn)
        => Publish(new(0, SubagentChangeKind.Turn, SessionId: sessionId, Turn: turn));

    private void OnProfileChanged(string profileId)
        => Publish(new(0, SubagentChangeKind.Profile, ProfileId: profileId));

    private void Publish(SubagentChanged change)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            change = change with
            {
                Revision = Interlocked.Increment(ref _revision),
                RuntimeInstanceId = _instanceId,
            };
            change = SubsessionAroundTurnPayload.FitChange(change);
            _replay.Enqueue(change);
            while (_replay.Count > ReplayCapacity)
            {
                _replay.Dequeue();
            }
            foreach (var subscriber in _subscribers.Values)
            {
                if (!subscriber.Channel.Writer.TryWrite(change))
                {
                    subscriber.Overflowed = true;
                    subscriber.Channel.Writer.TryComplete();
                }
            }
        }
    }

    private SubagentChanged CreateResnapshot(long revision)
        => new(
            revision,
            SubagentChangeKind.ResnapshotRequired,
            RuntimeInstanceId: _instanceId);

    private void OnRuntimeRetired(long generation)
    {
        lock (_runtimeGate)
        {
            if (generation != _runtimeGeneration) return;
            DetachRuntimeCore();
        }
        if (!Volatile.Read(ref _disposed))
        {
            try
            {
                _ = RefreshRuntime();
            }
            catch
            {
                // Catalog availability is reported by AgentRpcCatalog; retirement callbacks must not throw.
            }
        }
        Publish(new(0, SubagentChangeKind.ResnapshotRequired));
    }

    private bool RefreshRuntime()
    {
        if (Volatile.Read(ref _disposed)) return false;
        var runtimeReference = _rpcCatalog.GetServiceReferences(AgentRpcServices.RuntimeCatalogs)
            .FirstOrDefault();
        lock (_runtimeGate)
        {
            if (ReferenceEquals(_runtimeReference, runtimeReference)
                && _runtimeLease is { } currentLease
                && !currentLease.RetirementToken.IsCancellationRequested)
            {
                return false;
            }
        }

        AgentRpcLease<IAgentRuntimeCatalog>? runtimeLease = null;
        if (runtimeReference?.TryAcquire(out var acquired) == true) runtimeLease = acquired;

        long generation;
        lock (_runtimeGate)
        {
            if (Volatile.Read(ref _disposed))
            {
                runtimeLease?.Dispose();
                return false;
            }
            if (ReferenceEquals(_runtimeReference, runtimeReference)
                && _runtimeLease is { } currentLease
                && !currentLease.RetirementToken.IsCancellationRequested)
            {
                runtimeLease?.Dispose();
                return false;
            }

            var hadRuntime = _runtimeReference is not null;
            DetachRuntimeCore();
            if (runtimeReference is null || runtimeLease is null) return hadRuntime;
            _runtimeReference = runtimeReference;
            _runtimeLease = runtimeLease;
            _runtime = runtimeLease.Service;
            _runtime.SessionChanged += OnSessionChanged;
            _runtime.TurnChanged += OnTurnChanged;
            _runtime.ProfileChanged += OnProfileChanged;
            generation = ++_runtimeGeneration;
        }

        var registration = runtimeLease.RetirementToken.Register(
            static state =>
            {
                var (owner, registeredGeneration) =
                    ((SubagentRuntimeChangeStream Owner, long Generation))state!;
                owner.OnRuntimeRetired(registeredGeneration);
            },
            (this, generation));
        lock (_runtimeGate)
        {
            if (generation == _runtimeGeneration && ReferenceEquals(_runtimeLease, runtimeLease))
            {
                _runtimeRetirement = registration;
            }
            else
            {
                registration.Dispose();
            }
        }
        return true;
    }

    private void DetachRuntimeCore()
    {
        _runtimeGeneration++;
        _runtimeRetirement.Unregister();
        _runtimeRetirement = default;
        if (_runtime is { } runtime)
        {
            runtime.SessionChanged -= OnSessionChanged;
            runtime.TurnChanged -= OnTurnChanged;
            runtime.ProfileChanged -= OnProfileChanged;
        }
        _runtime = null;
        _runtimeReference = null;
        _runtimeLease?.Dispose();
        _runtimeLease = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Channel.Writer.TryComplete();
            }
            _subscribers.Clear();
        }

        _service.SubagentsChanged -= OnSubagentsChanged;
        _capabilities.Changed -= OnCatalogChanged;
        _capabilities.Dispose();
        lock (_runtimeGate) DetachRuntimeCore();
    }

    private sealed class Subscriber(Channel<SubagentChanged> channel)
    {
        public Channel<SubagentChanged> Channel { get; } = channel;
        public bool Overflowed { get; set; }
    }
}
