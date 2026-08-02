using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal sealed class RegressionTestPackageScope : IDisposable
{
    private RegressionTestPackageScope(string rootPath)
    {
        RootPath = rootPath;
        Context = new RegressionTestPackageContext(rootPath);
    }

    public string RootPath { get; }

    public IPackageContext Context { get; }

    public static RegressionTestPackageScope Create()
    {
        var temporaryRoot = OperatingSystem.IsMacOS()
            ? "/private" + Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
            : Path.GetTempPath();
        var rootPath = Path.Combine(
            temporaryRoot,
            "sunder-agent-regression-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        return new RegressionTestPackageScope(rootPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch
        {
            // Cleanup should not hide assertion failures.
        }
    }
}

internal class RegressionTestExtensionCatalog : AgentRpcCatalog
{
    private readonly RegressionTestRpcClient _client;

    public RegressionTestExtensionCatalog()
        : this(new RegressionTestRpcClient(new RegressionTestRpcState(), "test.consumer"))
    {
    }

    private RegressionTestExtensionCatalog(RegressionTestRpcClient client)
        : base(client)
    {
        _client = client;
        RunControls = new AgentRunControlRegistry();
        BehaviorLoops = AgentRpcServices.CreateBehaviorLoops(RunControls, this);
        AddProvider(
            AgentRpcContractIds.RunControl,
            RunControls,
            "sunder.package.agent",
            providerId: AgentRunControlRpc.ProviderId);
    }

    public AgentRunControlRegistry RunControls { get; }

    public AgentRpcProviderService<IAgentBehaviorLoop> BehaviorLoops { get; }

    public int DiscoveryCount => _client.State.DiscoveryCount;

    public Exception? LastInvocationFailure => _client.State.LastInvocationFailure;

    public AgentRpcReference<TService> GetRequiredReference<TService>(AgentRpcService<TService> service)
        where TService : class
        => Assert.Single(GetServiceReferences(service));

    public void AddProvider<TService>(
        AgentRpcService<TService> service,
        TService implementation,
        string packageId = "test.package")
        where TService : class
        => AddProvider(service.ContractId, implementation, packageId);

    public void AddProvider<TService>(
        AgentRpcProviderService<TService> service,
        TService implementation,
        string packageId = "test.package")
        where TService : class
        => AddProvider(service.ContractId, implementation, packageId);

    public void AddTool(IAgentTool tool, string packageId = "test.package")
    {
        var source = new AgentStaticToolSourceAdapter(
            "installed-packages",
            "Installed packages",
            "test",
            [tool]);
        AddProvider(AgentRpcContractIds.ToolSource, source, packageId, tool);
    }

    public void AddBehaviorLoop(IAgentBehaviorLoop implementation, string packageId = "test.package")
        => AddProvider(AgentRpcContractIds.BehaviorLoop, implementation, packageId);

    public Task RetireProviderAsync(object implementation)
        => _client.State.RetireAsync(implementation);

    public Task RetireProviderAsync<TService>(AgentRpcService<TService> _, TService implementation)
        where TService : class
        => RetireProviderAsync(implementation);

    public void RemoveProvider(object implementation)
        => _ = RetireProviderAsync(implementation);

    public void RemoveProvider<TService>(AgentRpcService<TService> _, TService implementation)
        where TService : class
        => RemoveProvider(implementation);

    public new void Dispose()
    {
        base.Dispose();
        _client.State.Dispose();
    }

    private void AddProvider(
        string contractId,
        object implementation,
        string packageId,
        object? registrationIdentity = null,
        string? providerId = null)
    {
        var providerCatalog = new AgentRpcCatalog(_client.ForCaller(packageId));
        var handler = contractId switch
        {
            AgentRpcContractIds.ChatProvider when implementation is IAgentChatProvider service => AgentChatProviderRpc.CreateHandler(service),
            AgentRpcContractIds.EmbeddingProvider when implementation is IAgentEmbeddingProvider service => AgentEmbeddingProviderRpc.CreateHandler(service),
            AgentRpcContractIds.RuntimeCatalog when implementation is IAgentRuntimeCatalog service => AgentRuntimeCatalogRpc.CreateHandler(service),
            AgentRpcContractIds.WorkspaceExecutionResolver when implementation is IAgentWorkspaceExecutionResolver service => AgentWorkspaceExecutionResolverRpc.CreateHandler(service),
            AgentRpcContractIds.ChildRunExecutor when implementation is IAgentChildRunExecutor service => AgentChildRunExecutorRpc.CreateHandler(service),
            AgentRpcContractIds.SessionCleaner when implementation is IAgentSessionDataCleaner service => AgentSessionCleanerRpc.CreateHandler(service),
            AgentRpcContractIds.SystemPromptContributor when implementation is IAgentSystemPromptContributor service => AgentSystemPromptContributorRpc.CreateHandler(service),
            AgentRpcContractIds.ToolSource when implementation is IAgentToolSource service => AgentToolSourceRpc.CreateHandler(service, providerCatalog),
            AgentRpcContractIds.PermissionSurface when implementation is IAgentPermissionSurface service => AgentPermissionSurfaceRpc.CreateHandler(service),
            AgentRpcContractIds.PromptContextContributor when implementation is IAgentPromptContextContributor service => AgentPromptContextContributorRpc.CreateHandler(service, providerCatalog),
            AgentRpcContractIds.DurableLifecycleObserver when implementation is IAgentDurableLifecycleObserver service => AgentDurableLifecycleObserverRpc.CreateHandler(service),
            AgentRpcContractIds.ProfileCapabilityConsumer when implementation is IAgentProfileCapabilityConsumer service => AgentProfileCapabilityConsumerRpc.CreateHandler(service),
            AgentRpcContractIds.SelectableCapabilityProvider when implementation is IAgentProfileSelectableCapabilityProvider service => AgentSelectableCapabilityProviderRpc.CreateHandler(service),
            AgentRpcContractIds.BehaviorLoop when implementation is IAgentBehaviorLoop service => AgentBehaviorLoopRpc.CreateHandler(service, providerCatalog),
            AgentRpcContractIds.ExecutionTarget when implementation is IAgentExecutionTarget service => AgentExecutionTargetRpc.CreateHandler(service),
            AgentRpcContractIds.WorkspacePathMigrator when implementation is IAgentWorkspacePathMigrationContributor service => AgentWorkspacePathMigratorRpc.CreateHandler(service),
            AgentRpcContractIds.WorkspaceEditor when implementation is IAgentWorkspaceEditorContributor service => AgentWorkspaceEditorRpc.CreateHandler(service),
            AgentRpcContractIds.RunControl when implementation is AgentRunControlRegistry service => AgentRunControlRpc.CreateHandler(service),
            _ => throw new ArgumentException($"No test RPC handler is available for contract '{contractId}'.", nameof(implementation)),
        };
        _client.State.Add(
            packageId,
            contractId,
            registrationIdentity ?? implementation,
            handler,
            providerCatalog,
            providerId);
    }
}

internal sealed class RegressionTestRpcClient(RegressionTestRpcState state, string callerPackageId) : ISunderRpcClient
{
    public RegressionTestRpcState State { get; } = state;

    public RegressionTestRpcClient ForCaller(string packageId) => new(State, packageId);

    public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ISunderRpcCallScope>(
            new RegressionTestRpcCallScope(State, callerPackageId, options));
    }

    public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(State.GetProvider(endpoint));

    public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(State.Discover(contractId));

    public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default)
        => State.WatchAsync(afterRevision, afterSequence, cancellationToken);

    public ValueTask<JsonElement> InvokeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        => State.InvokeAsync(callerPackageId, endpoint, serviceId, methodId, request, cancellationToken, scope: null);

    public IAsyncEnumerable<JsonElement> SubscribeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        => State.SubscribeAsync(callerPackageId, endpoint, serviceId, methodId, request, cancellationToken, scope: null);
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
        return ValueTask.FromResult(state.GetProvider(endpoint));
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
        using var invocation = CreateInvocationCancellation(cancellationToken);
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
        using var invocation = CreateInvocationCancellation(cancellationToken);
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
        using var invocation = CreateInvocationCancellation(cancellationToken);
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

    private CancellationTokenSource CreateInvocationCancellation(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable(cancellationToken);
        var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var remaining = DeadlineUtc - DateTimeOffset.UtcNow;
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

internal sealed class RegressionTestRpcState : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<ChannelWriter<SunderRpcCatalogEvent>> _watchers = [];
    private readonly List<SunderRpcCatalogEvent> _events = [];
    private long _revision;
    private long _sequence;
    private int _providerSequence;
    private int _discoveryCount;
    private Exception? _lastInvocationFailure;

    public int DiscoveryCount => Volatile.Read(ref _discoveryCount);

    public Exception? LastInvocationFailure => Volatile.Read(ref _lastInvocationFailure);

    public void Add(
        string packageId,
        string contractId,
        object implementation,
        ISunderRpcServiceHandler handler,
        IDisposable ownedCatalog,
        string? providerId = null)
    {
        lock (_gate)
        {
            var sequence = ++_providerSequence;
            var endpoint = new SunderRpcEndpointReference($"test.rpc.{Guid.NewGuid():N}");
            var descriptor = AgentRpcContractDescriptors.Get(contractId);
            var snapshot = new SunderRpcProviderSnapshot(
                packageId,
                "1.0.0",
                providerId ?? $"test.provider.{sequence}",
                contractId,
                descriptor.Version,
                descriptor.Sha256,
                Guid.NewGuid(),
                sequence,
                1,
                endpoint,
                ++_revision,
                SunderRpcProviderState.Active);
            var entry = new Entry(implementation, handler, snapshot, descriptor, ownedCatalog);
            _entries.Add(endpoint.Value, entry);
            Publish(new SunderRpcCatalogEvent(_revision, ++_sequence, SunderRpcCatalogEventKind.Activated, snapshot));
        }
    }

    public SunderRpcProviderSnapshot? GetProvider(SunderRpcEndpointReference endpoint)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(endpoint.Value, out var entry) && entry.Active
                ? entry.Snapshot
                : null;
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
                    .Where(entry => entry.Active && string.Equals(entry.Snapshot.ContractId, contractId, StringComparison.Ordinal))
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
            foreach (var item in _events.Where(item =>
                         item.Revision > afterRevision
                         || (item.Revision == afterRevision && item.Sequence > afterSequence)))
            {
                channel.Writer.TryWrite(item);
            }
            _watchers.Add(channel.Writer);
        }
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item;
        }
        finally
        {
            lock (_gate) _watchers.Remove(channel.Writer);
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
        var entry = Acquire(endpoint);
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.Retirement.Token);
        var context = CreateContext(callerPackageId, entry.Snapshot, invocation.Token, scope);
        try
        {
            ValidatePayload(entry, serviceId, methodId, request, output: false);
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
        catch (Exception exception)
        {
            Volatile.Write(ref _lastInvocationFailure, exception);
            throw;
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
        var entry = Acquire(endpoint);
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.Retirement.Token);
        var context = CreateContext(callerPackageId, entry.Snapshot, invocation.Token, scope);
        try
        {
            ValidatePayload(entry, serviceId, methodId, request, output: false);
            await foreach (var item in entry.Handler.InvokeServerStreamAsync(
                               context,
                               serviceId,
                               methodId,
                               request,
                               invocation.Token).WithCancellation(invocation.Token).ConfigureAwait(false))
            {
                ValidatePayload(entry, serviceId, methodId, item, output: true);
                yield return item;
            }
        }
        finally
        {
            context.Revoke();
            Release(entry);
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

    private static void ValidatePayload(
        Entry entry,
        string serviceId,
        string methodId,
        JsonElement payload,
        bool output)
    {
        var method = entry.Descriptor.FindService(serviceId)?.FindMethod(methodId);
        var schemaReference = output ? method?.OutputSchemaReference : method?.RequestSchemaReference;
        string? error = null;
        if (schemaReference is null
            || !entry.Descriptor.IsValid(schemaReference, payload, out error))
        {
            throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.Validation,
                "test.rpc.schema-validation",
                $"The RPC {(output ? "output" : "request")} does not match "
                + $"'{entry.Descriptor.ContractId}/{serviceId}/{methodId}': {error ?? "method not found"}"));
        }
    }

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

internal sealed class RegressionTestPackageContext(string rootPath) : IPackageContext
{
    public string PackageId => "test.package.agent";

    public string Version { get; } = "1.0.0";

    public string ContentRootPath => AppContext.BaseDirectory;

    public IPackageStorageContext Storage { get; } = new RegressionTestStorageContext(rootPath);

    public IPackageSettings Settings { get; } = new RegressionTestSettings();

    public IPackageSecrets Secrets { get; } = new RegressionTestSecrets();


    public Sunder.Sdk.Logging.IPackageLogging Logging { get; } =
        Sunder.Sdk.Logging.NullPackageLogging.Instance;
}

internal sealed class RegressionTestStorageContext : IPackageStorageContext
{
    public RegressionTestStorageContext(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        Files = new RegressionTestFileStore(Path.Combine(rootPath, "files"));
        RoleLocalWorkspace = new TestPackageRoleLocalWorkspace(rootPath);
    }

    public IPackageFileStore Files { get; }

    public IPackageKeyValueStore State { get; } = new RegressionTestKeyValueStore();

    public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; }
}

internal sealed class RegressionTestFileStore(string rootPath) : IPackageFileStore
{
    private readonly string _rootPath = rootPath;

    public async Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        TestPackageStorageGuards.RelativePath(relativePath);
        var path = Path.Combine(_rootPath, relativePath);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
    }

    public async Task WriteAsync(string relativePath, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
    {
        TestPackageStorageGuards.RelativePath(relativePath);
        TestPackageStorageGuards.FileLength(contents.Length);
        var path = Path.Combine(_rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, contents.ToArray(), cancellationToken);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.RelativePath(relativePath);
        File.Delete(Path.Combine(_rootPath, relativePath));
        return Task.CompletedTask;
    }
}

internal sealed class RegressionTestKeyValueStore : IPackageKeyValueStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
    private WriteBlock? _nextWrite;

    public WriteBlock BlockNextWrite()
    {
        var block = new WriteBlock();
        Assert.Null(Interlocked.CompareExchange(ref _nextWrite, block, null));
        return block;
    }

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Key(key);
        return Task.FromResult(_values.GetValueOrDefault(key));
    }

    public async Task SetValueAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Key(key);
        TestPackageStorageGuards.Value(value);
        var block = Interlocked.Exchange(ref _nextWrite, null);
        if (block is not null)
        {
            block.Started.TrySetResult();
            await block.Release.Task.WaitAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        _values[key] = value;
    }

    public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Key(key);
        return Task.FromResult(_values.ContainsKey(key));
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Key(key);
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Prefix(prefix);
        return Task.FromResult<IReadOnlyList<string>>(
            _values.Keys
                .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    internal sealed class WriteBlock
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class RegressionTestSettings : EmptyPackageSettings
{
}

internal sealed class RegressionTestSecrets : IPackageSecrets
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Key(key);
        return Task.FromResult(_values.GetValueOrDefault(key));
    }

    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Key(key);
        TestPackageStorageGuards.Value(value);
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestPackageStorageGuards.Key(key);
        _values.Remove(key);
        return Task.CompletedTask;
    }
}
