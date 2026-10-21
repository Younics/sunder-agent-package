using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal sealed class RegressionTestRpcClient(
    RegressionTestRpcState state,
    string callerPackageId,
    SunderRpcResourceProfile? resourceProfile = null) : ISunderRpcClient
{
    public RegressionTestRpcState State { get; } = state;

    public SunderRpcResourceProfile? ResourceProfile { get; } = resourceProfile;

    public RegressionTestRpcClient ForCaller(string packageId) => new(State, packageId, ResourceProfile);

    public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ISunderRpcCallScope>(
            new RegressionTestRpcCallScope(State, callerPackageId, options));
    }

    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(State.GetProvider(endpoint, callerPackageId));
    }

    public ValueTask<bool> TryReportInvariantViolationAsync(
        SunderRpcEndpointReference endpoint,
        Exception exception,
        CancellationToken cancellationToken = default)
        => State.TryReportInvariantViolationAsync(
            callerPackageId,
            endpoint,
            exception,
            cancellationToken);

    public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(State.Discover(contractId));
    }

    public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
        => State.WatchAsync(afterRevision, afterSequence, cancellationToken);

    public async ValueTask<JsonElement> InvokeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = CreateInvocationCancellation(options, cancellationToken);
        return await State.InvokeAsync(
            callerPackageId,
            endpoint,
            serviceId,
            methodId,
            request,
            invocation.Token,
            scope: null).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<JsonElement> SubscribeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var invocation = CreateInvocationCancellation(options, cancellationToken);
        await foreach (var item in State.SubscribeAsync(
                           callerPackageId,
                           endpoint,
                           serviceId,
                           methodId,
                           request,
                           invocation.Token,
                           scope: null).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private static CancellationTokenSource CreateInvocationCancellation(
        SunderRpcCallOptions? options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options?.DeadlineUtc is { } deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                invocation.Cancel();
            }
            else
            {
                invocation.CancelAfter(remaining);
            }
        }
        return invocation;
    }
}

internal sealed class RegressionTestRpcCallScope(
    RegressionTestRpcState state,
    string callerPackageId,
    SunderRpcCallOptions? options) : ISunderRpcCallScope
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, ContentEntry> _requestContent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentEntry> _responseContent = new(StringComparer.Ordinal);
    private int _disposed;

    public DateTimeOffset DeadlineUtc { get; } = options?.DeadlineUtc ?? DateTimeOffset.UtcNow.AddMinutes(5);

    public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(
        SunderRpcCallOptions? nestedOptions = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable(cancellationToken);
        return ValueTask.FromResult<ISunderRpcCallScope>(
            new RegressionTestRpcCallScope(state, callerPackageId, nestedOptions));
    }

    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable(cancellationToken);
        return ValueTask.FromResult(state.GetProvider(endpoint, callerPackageId));
    }

    public ValueTask<bool> TryReportInvariantViolationAsync(
        SunderRpcEndpointReference endpoint,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable(cancellationToken);
        return state.TryReportInvariantViolationAsync(
            callerPackageId,
            endpoint,
            exception,
            cancellationToken);
    }

    public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
        string contractId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable(cancellationToken);
        return ValueTask.FromResult(state.Discover(contractId));
    }

    public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
        long afterRevision,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var invocation = CreateInvocationCancellation(null, cancellationToken);
        await foreach (var item in state.WatchAsync(
                           afterRevision,
                           afterSequence,
                           invocation.Token).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public async ValueTask<JsonElement> InvokeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var invocation = CreateInvocationCancellation(options, cancellationToken);
        return await state.InvokeAsync(
            callerPackageId,
            endpoint,
            serviceId,
            methodId,
            request,
            invocation.Token,
            this).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<JsonElement> SubscribeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var invocation = CreateInvocationCancellation(options, cancellationToken);
        await foreach (var item in state.SubscribeAsync(
                           callerPackageId,
                           endpoint,
                           serviceId,
                           methodId,
                           request,
                           invocation.Token,
                           this).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public async ValueTask<SunderRpcContentReference> RegisterContentAsync(
        SunderRpcEndpointReference endpoint,
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfUnavailable(cancellationToken);
        if (state.GetProvider(endpoint) is null)
        {
            throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.StaleEndpoint,
                "test.rpc.stale-endpoint",
                "The test RPC provider activation is stale."));
        }

        await using var destination = new MemoryStream();
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return StoreContent(_requestContent, endpoint, destination.ToArray(), options);
    }

    public async ValueTask<SunderRpcContentReference> RegisterContentFileAsync(
        SunderRpcEndpointReference endpoint,
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var source = File.OpenRead(filePath);
        return await RegisterContentAsync(endpoint, source, options, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<Stream> OpenContentAsync(
        SunderRpcContentReference reference,
        CancellationToken cancellationToken = default)
        => OpenContentAsync(_responseContent, reference, endpoint: null, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _lifetime.Cancel();
        lock (_gate)
        {
            _requestContent.Clear();
            _responseContent.Clear();
        }
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }

    internal ISunderRpcInvocationAuthority CreateInvocationAuthority(SunderRpcEndpointReference endpoint)
        => new RegressionTestInvocationAuthority(this, endpoint);

    internal ValueTask<Stream> OpenRequestContentAsync(
        SunderRpcEndpointReference endpoint,
        SunderRpcContentReference reference,
        CancellationToken cancellationToken)
        => OpenContentAsync(_requestContent, reference, endpoint, cancellationToken);

    internal async ValueTask<SunderRpcContentReference> RegisterResponseContentAsync(
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfUnavailable(cancellationToken);
        state.ThrowNextResponseContentRegistrationFailure();
        await using var destination = new MemoryStream();
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return StoreContent(_responseContent, endpoint: null, destination.ToArray(), options);
    }

    private SunderRpcContentReference StoreContent(
        Dictionary<string, ContentEntry> destination,
        SunderRpcEndpointReference? endpoint,
        byte[] content,
        SunderRpcContentRegistrationOptions options)
    {
        if (options.Length is { } expectedLength && expectedLength != content.LongLength)
        {
            throw new InvalidDataException("The registered RPC content length does not match its declared length.");
        }

        var expiresAtUtc = options.ExpiresAtUtc is { } requestedExpiry && requestedExpiry < DeadlineUtc
            ? requestedExpiry
            : DeadlineUtc;
        var maximumUses = options.Repeatability == SunderRpcContentRepeatability.SingleUse
            ? 1
            : options.MaximumUses;
        if (maximumUses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RPC content must permit at least one use.");
        }

        var reference = new SunderRpcContentReference(
            $"test.rpc.content.{Guid.NewGuid():N}",
            content.LongLength,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            options.MediaType,
            options.FileName,
            expiresAtUtc,
            options.Repeatability);
        lock (_gate)
        {
            ThrowIfDisposed();
            destination.Add(reference.Id, new ContentEntry(reference, endpoint, content, maximumUses));
        }
        return reference;
    }

    private ValueTask<Stream> OpenContentAsync(
        Dictionary<string, ContentEntry> source,
        SunderRpcContentReference reference,
        SunderRpcEndpointReference? endpoint,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable(cancellationToken);
        byte[] content;
        lock (_gate)
        {
            if (!source.TryGetValue(reference.Id, out var entry)
                || entry.Reference != reference
                || entry.Endpoint != endpoint
                || entry.Reference.ExpiresAtUtc <= DateTimeOffset.UtcNow
                || entry.RemainingUses <= 0)
            {
                throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.NotFound,
                    "test.rpc.content-unavailable",
                    "The test RPC content reference is unavailable."));
            }

            entry.RemainingUses--;
            if (entry.RemainingUses == 0)
            {
                source.Remove(reference.Id);
            }
            content = entry.Content;
        }
        return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
    }

    private CancellationTokenSource CreateInvocationCancellation(
        SunderRpcCallOptions? options,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable(cancellationToken);
        var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var deadline = options?.DeadlineUtc is { } callDeadline && callDeadline < DeadlineUtc
            ? callDeadline
            : DeadlineUtc;
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            invocation.Cancel();
        }
        else
        {
            invocation.CancelAfter(remaining);
        }
        return invocation;
    }

    private void ThrowIfUnavailable(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        _lifetime.Token.ThrowIfCancellationRequested();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class ContentEntry(
        SunderRpcContentReference reference,
        SunderRpcEndpointReference? endpoint,
        byte[] content,
        int remainingUses)
    {
        public SunderRpcContentReference Reference { get; } = reference;
        public SunderRpcEndpointReference? Endpoint { get; } = endpoint;
        public byte[] Content { get; } = content;
        public int RemainingUses { get; set; } = remainingUses;
    }
}

internal sealed class RegressionTestInvocationAuthority(
    RegressionTestRpcCallScope scope,
    SunderRpcEndpointReference endpoint) : ISunderRpcInvocationAuthority
{
    private readonly CancellationTokenSource _revocation = new();

    public CancellationToken RevocationToken => _revocation.Token;

    public ValueTask<SunderRpcContentReference> RegisterContentAsync(
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken)
    {
        ThrowIfRevoked();
        return scope.RegisterResponseContentAsync(source, options, cancellationToken);
    }

    public async ValueTask<SunderRpcContentReference> RegisterContentFileAsync(
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken)
    {
        ThrowIfRevoked();
        await using var source = File.OpenRead(filePath);
        return await RegisterContentAsync(source, options, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<Stream> OpenContentAsync(
        SunderRpcContentReference reference,
        CancellationToken cancellationToken)
    {
        ThrowIfRevoked();
        return scope.OpenRequestContentAsync(endpoint, reference, cancellationToken);
    }

    public void Revoke() => _revocation.Cancel();

    private void ThrowIfRevoked() => _revocation.Token.ThrowIfCancellationRequested();
}

internal sealed partial class RegressionTestRpcState(bool workerStyleProviderBoundary = false) : IDisposable
{
    private readonly bool _workerStyleProviderBoundary = workerStyleProviderBoundary;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Caller, string? Endpoint), SunderRpcErrorKind> _providerLookupFailures = new();
    private readonly List<ChannelWriter<SunderRpcCatalogEvent>> _watchers = [];
    private readonly List<SunderRpcCatalogEvent> _events = [];
    private long _revision;
    private long _sequence;
    private int _providerSequence;
    private int _discoveryCount;
    private int _invariantViolationReportCount;
    private Exception? _lastInvocationFailure;
    private int _invocationAttemptCount;
    private int _streamInvocationAttemptCount;
    private JsonElement _lastInvocationRequest;
    private JsonElement _lastStreamInvocationRequest;

    internal int InvocationAttemptCount => Volatile.Read(ref _invocationAttemptCount);
    internal int StreamInvocationAttemptCount => Volatile.Read(ref _streamInvocationAttemptCount);
    // Passive fixture readiness: signalled only after the real provider produced
    // and passed validation for a stream item. Invocation entry is too early.
    internal event Action<string, SunderRpcProviderSnapshot, string, string>? ProviderStreamItemObserved;
    internal JsonElement LastInvocationRequest { get { lock (_gate) return _lastInvocationRequest; } }
    internal JsonElement LastStreamInvocationRequest { get { lock (_gate) return _lastStreamInvocationRequest; } }
    private SunderRpcEndpointReference? _lastInvariantViolationEndpoint;
    private string? _lastInvariantViolationPackageId;
    private Exception? _nextResponseContentRegistrationFailure;

    public int DiscoveryCount => Volatile.Read(ref _discoveryCount);

    public Exception? LastInvocationFailure => Volatile.Read(ref _lastInvocationFailure);

    public SunderRpcEndpointReference? LastInvariantViolationEndpoint
        => Volatile.Read(ref _lastInvariantViolationEndpoint);

    public string? LastInvariantViolationPackageId
        => Volatile.Read(ref _lastInvariantViolationPackageId);

    public int InvariantViolationReportCount => Volatile.Read(ref _invariantViolationReportCount);

    public void FailNextResponseContentRegistration(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Interlocked.CompareExchange(
                ref _nextResponseContentRegistrationFailure,
                exception,
                null) is not null)
        {
            throw new InvalidOperationException(
                "A response-content registration failure is already pending.");
        }
    }

    internal void ThrowNextResponseContentRegistrationFailure()
    {
        if (Interlocked.Exchange(ref _nextResponseContentRegistrationFailure, null) is { } exception)
        {
            throw exception;
        }
    }

    public SunderRpcProviderSnapshot Add(
        string packageId,
        string contractId,
        object implementation,
        ISunderRpcServiceHandler handler,
        IDisposable ownedCatalog,
        string? providerId = null)
        => Add(
            packageId,
            CurrentPrivateRpcDescriptors.Get(contractId),
            implementation,
            handler,
            ownedCatalog,
            providerId);

    public SunderRpcProviderSnapshot AddGenerated(
        string packageId,
        object implementation,
        SunderWorkerProviderRegistration registration,
        IDisposable ownedCatalog,
        string? providerId = null)
    {
        var descriptor = CurrentPrivateRpcDescriptors.Get(registration.ContractId);
        Assert.Equal(descriptor.Version, registration.ContractVersion);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        return Add(packageId, descriptor, implementation, registration.Handler, ownedCatalog, providerId, "2.0.0");
    }

    public SunderRpcProviderSnapshot Add(
        string packageId,
        SunderRpcContractDescriptor descriptor,
        object implementation,
        ISunderRpcServiceHandler handler,
        IDisposable ownedCatalog,
        string? providerId = null,
        string packageVersion = "1.0.0")
    {
        lock (_gate)
        {
            var sequence = ++_providerSequence;
            var endpoint = new SunderRpcEndpointReference($"test.rpc.{Guid.NewGuid():N}");
            var snapshot = new SunderRpcProviderSnapshot(
                packageId,
                packageVersion,
                providerId ?? $"test.provider.{sequence}",
                descriptor.ContractId,
                descriptor.Version,
                descriptor.Sha256,
                descriptor.AbiSha256 ?? throw new InvalidOperationException(
                    $"Agent RPC contract '{descriptor.ContractId}' does not expose an ABI SHA-256."),
                Guid.NewGuid(),
                sequence,
                1,
                endpoint,
                ++_revision,
                SunderRpcProviderState.Active);
            var entry = new Entry(implementation, handler, snapshot, descriptor, ownedCatalog);
            _entries.Add(endpoint.Value, entry);
            Publish(new SunderRpcCatalogEvent(_revision, ++_sequence, SunderRpcCatalogEventKind.Activated, snapshot));
            return snapshot;
        }
    }

    public void SetProviderLookupFailure(string callerPackageId, SunderRpcErrorKind? errorKind, SunderRpcEndpointReference? endpoint = null)
    {
        lock (_gate)
        {
            if (errorKind is { } value)
            {
                _providerLookupFailures[(callerPackageId, endpoint?.Value)] = value;
            }
            else
            {
                _providerLookupFailures.Remove((callerPackageId, endpoint?.Value));
            }
        }
    }

    public SunderRpcProviderSnapshot? GetProvider(
        SunderRpcEndpointReference endpoint,
        string? callerPackageId = null)
    {
        lock (_gate)
        {
            if (callerPackageId is not null
                && (_providerLookupFailures.TryGetValue((callerPackageId, endpoint.Value), out var errorKind)
                    || _providerLookupFailures.TryGetValue((callerPackageId, null), out errorKind)))
            {
                throw new SunderRpcException(new SunderRpcError(
                    errorKind,
                    "test.rpc.provider-lookup-failure",
                    $"Injected {errorKind} exact-provider lookup failure."));
            }
            return _entries.TryGetValue(endpoint.Value, out var entry) && entry.Active
                ? entry.Snapshot
                : null;
        }
    }

    public ValueTask<bool> TryReportInvariantViolationAsync(
        string callerPackageId,
        SunderRpcEndpointReference endpoint,
        Exception exception,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(callerPackageId, "younics.ai.agent", StringComparison.Ordinal))
        {
            return ValueTask.FromResult(false);
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(endpoint.Value, out var entry) || !entry.Active)
            {
                return ValueTask.FromResult(false);
            }

            entry.Active = false;
            entry.Retirement.Cancel();
            var snapshot = entry.Snapshot with
            {
                CatalogRevision = ++_revision,
                State = SunderRpcProviderState.Faulted,
                FaultCode = "rpc.provider.invariant-violation",
            };
            Volatile.Write(ref _lastInvariantViolationEndpoint, endpoint);
            Volatile.Write(ref _lastInvariantViolationPackageId, entry.Snapshot.PackageId);
            Interlocked.Increment(ref _invariantViolationReportCount);
            Publish(new SunderRpcCatalogEvent(
                _revision,
                ++_sequence,
                SunderRpcCatalogEventKind.Faulted,
                snapshot));
            if (entry.ActiveCalls == 0) entry.Drained.TrySetResult();
            return ValueTask.FromResult(true);
        }
    }

    public SunderRpcCatalogSnapshot Discover(string contractId)
    {
        Interlocked.Increment(ref _discoveryCount);
        lock (_gate)
        {
            return new SunderRpcCatalogSnapshot(
                _revision,
                _sequence,
                _entries.Values
                    .Where(entry => entry.Active && (string.IsNullOrEmpty(contractId)
                        || string.Equals(entry.Snapshot.ContractId, contractId, StringComparison.Ordinal)))
                    .Select(static entry => entry.Snapshot));
        }
    }

    public async Task RetireAsync(object implementation)
    {
        Entry[] removed;
        lock (_gate)
        {
            removed = _entries.Values
                .Where(entry => entry.Active && ReferenceEquals(entry.Implementation, implementation))
                .ToArray();
            foreach (var entry in removed)
            {
                entry.Active = false;
                entry.Retirement.Cancel();
                var snapshot = entry.Snapshot with
                {
                    CatalogRevision = ++_revision,
                    State = SunderRpcProviderState.Inactive,
                };
                Publish(new SunderRpcCatalogEvent(_revision, ++_sequence, SunderRpcCatalogEventKind.Deactivated, snapshot));
                if (entry.ActiveCalls == 0) entry.Drained.TrySetResult();
            }
        }
        await Task.WhenAll(removed.Select(static entry => entry.Drained.Task)).ConfigureAwait(false);
        foreach (var entry in removed) entry.OwnedCatalog.Dispose();
    }

    public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
        long afterRevision,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<SunderRpcCatalogEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        lock (_gate)
        {
            var exactCursor = afterRevision == _revision && afterSequence == _sequence
                || afterRevision == 0 && afterSequence == 0 && (_events.Count == 0 || _events[0].Sequence == 1)
                || _events.Any(item => item.Revision == afterRevision && item.Sequence == afterSequence);
            if (!exactCursor)
                channel.Writer.TryWrite(new(_revision, _sequence, SunderRpcCatalogEventKind.ResetRequired, null));
            else
                foreach (var item in _events.Where(item => item.Sequence > afterSequence)) channel.Writer.TryWrite(item);
            _watchers.Add(channel.Writer);
        }
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item;
        }
        finally
        {
            lock (_gate) _watchers.Remove(channel.Writer);
            channel.Writer.TryComplete();
        }
    }

    internal void ResetCatalogReplay()
    {
        lock (_gate)
        {
            _events.Clear();
            Publish(new(++_revision, ++_sequence, SunderRpcCatalogEventKind.ResetRequired, null));
        }
    }

    public async ValueTask<JsonElement> InvokeAsync(
        string callerPackageId,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken,
        RegressionTestRpcCallScope? scope)
    {
        Interlocked.Increment(ref _invocationAttemptCount);
        lock (_gate) _lastInvocationRequest = request.ValueKind == JsonValueKind.Undefined ? default : request.Clone();
        var entry = Acquire(endpoint);
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.Retirement.Token);
        var context = CreateContext(callerPackageId, entry.Snapshot, invocation.Token, scope);
        var providerInvoked = false;
        try
        {
            ValidatePayload(entry, serviceId, methodId, request, output: false);
            providerInvoked = true;
            var response = await entry.Handler.InvokeUnaryAsync(
                context,
                serviceId,
                methodId,
                request,
                invocation.Token).ConfigureAwait(false);
            ValidatePayload(entry, serviceId, methodId, response, output: true);
            return response;
        }
        catch (OperationCanceledException) when (entry.Retirement.IsCancellationRequested)
        {
            var exception = new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.StaleEndpoint,
                "test.rpc.stale-endpoint",
                "The test RPC provider activation is stale."));
            Volatile.Write(ref _lastInvocationFailure, exception);
            throw exception;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref _lastInvocationFailure, exception);
            throw;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _lastInvocationFailure, exception);
            // Host-side request validation precedes dispatch. Only failures from
            // the provider/output boundary receive worker-style sanitization.
            var projected = providerInvoked ? ProjectProviderFailure(entry, serviceId, methodId, exception) : exception;
            if (ReferenceEquals(projected, exception)) throw;
            throw projected;
        }
        finally
        {
            context.Revoke();
            Release(entry);
        }
    }

    public async IAsyncEnumerable<JsonElement> SubscribeAsync(
        string callerPackageId,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        RegressionTestRpcCallScope? scope)
    {
        Interlocked.Increment(ref _streamInvocationAttemptCount);
        lock (_gate) _lastStreamInvocationRequest = request.Clone();
        var entry = Acquire(endpoint);
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.Retirement.Token);
        var context = CreateContext(callerPackageId, entry.Snapshot, invocation.Token, scope);
        try
        {
            ValidatePayload(entry, serviceId, methodId, request, output: false);
            await using var enumerator = entry.Handler.InvokeServerStreamAsync(
                    context,
                    serviceId,
                    methodId,
                    request,
                    invocation.Token)
                .GetAsyncEnumerator(invocation.Token);
            while (await MoveNextProviderStreamAsync(
                       entry,
                       serviceId,
                       methodId,
                       enumerator,
                       cancellationToken).ConfigureAwait(false))
            {
                var item = enumerator.Current;
                ValidateProviderOutput(entry, serviceId, methodId, item);
                ProviderStreamItemObserved?.Invoke(callerPackageId, entry.Snapshot, serviceId, methodId);
                yield return item;
            }
        }
        finally
        {
            context.Revoke();
            Release(entry);
        }
    }

    private async ValueTask<bool> MoveNextProviderStreamAsync(
        Entry entry,
        string serviceId,
        string methodId,
        IAsyncEnumerator<JsonElement> enumerator,
        CancellationToken cancellationToken)
    {
        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (entry.Retirement.IsCancellationRequested)
        {
            var exception = new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.StaleEndpoint,
                "test.rpc.stale-endpoint",
                "The test RPC provider activation is stale."));
            Volatile.Write(ref _lastInvocationFailure, exception);
            throw exception;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref _lastInvocationFailure, exception);
            throw;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _lastInvocationFailure, exception);
            var projected = ProjectProviderFailure(entry, serviceId, methodId, exception);
            if (ReferenceEquals(projected, exception)) throw;
            throw projected;
        }
    }

    public void Dispose()
    {
        Entry[] entries;
        ChannelWriter<SunderRpcCatalogEvent>[] watchers;
        lock (_gate)
        {
            entries = _entries.Values.ToArray();
            _entries.Clear();
            _providerLookupFailures.Clear();
            watchers = _watchers.ToArray();
            _watchers.Clear();
        }
        foreach (var entry in entries)
        {
            entry.Retirement.Cancel();
            entry.Retirement.Dispose();
            entry.OwnedCatalog.Dispose();
        }
        foreach (var watcher in watchers) watcher.TryComplete();
    }

    private Entry Acquire(SunderRpcEndpointReference endpoint)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(endpoint.Value, out var entry) || !entry.Active)
            {
                throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.StaleEndpoint,
                    "test.rpc.stale-endpoint",
                    "The test RPC provider activation is stale."));
            }
            entry.ActiveCalls++;
            return entry;
        }
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            entry.ActiveCalls--;
            if (!entry.Active && entry.ActiveCalls == 0) entry.Drained.TrySetResult();
        }
    }

    private void Publish(SunderRpcCatalogEvent item)
    {
        _events.Add(item);
        foreach (var watcher in _watchers.ToArray()) watcher.TryWrite(item);
    }

    private static SunderRpcInvocationContext CreateContext(
        string callerPackageId,
        SunderRpcProviderSnapshot provider,
        CancellationToken cancellationToken,
        RegressionTestRpcCallScope? scope)
        => new(
            callerPackageId,
            "1.0.0",
            provider,
            scope?.DeadlineUtc ?? DateTimeOffset.UtcNow.AddMinutes(1),
            1,
            cancellationToken,
            scope?.CreateInvocationAuthority(provider.Endpoint));

    private sealed class Entry(
        object implementation,
        ISunderRpcServiceHandler handler,
        SunderRpcProviderSnapshot snapshot,
        SunderRpcContractDescriptor descriptor,
        IDisposable ownedCatalog)
    {
        public object Implementation { get; } = implementation;
        public ISunderRpcServiceHandler Handler { get; } = handler;
        public SunderRpcProviderSnapshot Snapshot { get; } = snapshot;
        public SunderRpcContractDescriptor Descriptor { get; } = descriptor;
        public IDisposable OwnedCatalog { get; } = ownedCatalog;
        public CancellationTokenSource Retirement { get; } = new();
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActiveCalls { get; set; }
        public bool Active { get; set; } = true;
    }
}
