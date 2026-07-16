using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Subagents.Runtime;

internal interface ISubagentManagementGateway
{
    event Action? SubagentsChanged;
    event Action? CatalogChanged;
    Task<IReadOnlyList<SubagentRecord>> ListSubagentsAsync(CancellationToken cancellationToken = default);
    Task<SubagentRecord> CreateSubagentAsync(string displayName, CancellationToken cancellationToken = default);
    Task<SubagentRecord> SaveSubagentAsync(SubagentSaveRequest request, CancellationToken cancellationToken = default);
    Task DeleteSubagentAsync(string subagentId, CancellationToken cancellationToken = default);
    IReadOnlyList<ProviderCatalogOption> ListChatProviders();
    Task<ProviderModelCatalogResult> LoadChatModelsAsync(string providerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentToolDescriptor>> ListLocalToolsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListPackageCapabilitiesAsync(CancellationToken cancellationToken = default);
}

internal interface ISubsessionSessionReader
{
    Task<SubsessionSessionCatalog> ListSessionsAsync(CancellationToken cancellationToken = default);
}

internal interface ISubsessionCheckpointReader
{
    Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(
        CancellationToken cancellationToken = default);
}

internal interface ISubsessionTranscriptPageReader
{
    Task<IReadOnlyList<AgentTurnRecord>> ListRecentTurnsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentTurnRecord>> ListTurnsBeforeAsync(
        Guid sessionId,
        DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentTurnRecord>> ListTurnsAfterAsync(
        Guid sessionId,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId,
        int limit,
        CancellationToken cancellationToken = default);
}

internal interface ISubsessionChangeNotifications
{
    event Action<Guid>? SessionChanged;
    event Action<Guid, AgentTurnRecord>? TurnChanged;
}

internal interface ISubagentPresentationInitialization
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

internal sealed record SubsessionSessionCatalog(
    IReadOnlyList<AgentSessionRecord> Sessions,
    IReadOnlyList<AgentProfileRecord> Profiles);

internal sealed record SubagentSaveRequest(
    string SubagentId,
    string DisplayName,
    string? Description,
    string? Instructions,
    string? ChatProviderId,
    string? ChatModelId,
    IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? Assignments,
    string? ChatModelSettingsJson);

internal static class SubagentRuntimeOperations
{
    internal static readonly PackageRuntimeOperation<SubagentQuery, SubagentProjection> Query = new("subagents.query.v1");
    internal static readonly PackageRuntimeOperation<SubagentCommand, SubagentProjection> Command = new("subagents.command.v1");
    internal static readonly PackageRuntimeStream<SubagentChangeSubscription, SubagentChanged> Changes = new("subagents.changes.v1");
}

internal enum SubagentQueryKind { Management, ProviderModels, RuntimeCatalog, RecentTurns, TurnsBefore, TurnsAfter }
internal sealed record SubagentQuery(
    SubagentQueryKind Kind,
    string? ProviderId = null,
    Guid? SessionId = null,
    int Limit = 100,
    DateTimeOffset? AnchorCreatedAtUtc = null,
    Guid? AnchorTurnId = null);

internal enum SubagentCommandKind { Create, Save, Delete }
internal sealed record SubagentCommand(SubagentCommandKind Kind, string? Value = null, SubagentSaveRequest? Save = null);

internal sealed record SubagentProviderProjection(
    string ProviderId,
    string DisplayName,
    string? PackageId,
    IReadOnlyList<AgentModelDescriptor>? Models = null,
    AgentProviderReadiness? Readiness = null);

internal sealed record SubagentProjection(
    IReadOnlyList<SubagentRecord>? Subagents = null,
    SubagentRecord? Subagent = null,
    IReadOnlyList<SubagentProviderProjection>? Providers = null,
    IReadOnlyList<AgentToolDescriptor>? Tools = null,
    IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>? Capabilities = null,
    IReadOnlyList<AgentSessionRecord>? Sessions = null,
    IReadOnlyList<AgentProfileRecord>? Profiles = null,
    IReadOnlyList<AgentRunCheckpointRecord>? Checkpoints = null,
    IReadOnlyList<AgentTurnRecord>? Turns = null);

internal sealed record SubagentChangeSubscription;
internal enum SubagentChangeKind { Subagents, Catalog, Session, Turn, Profile }
internal sealed record SubagentChanged(SubagentChangeKind Kind, Guid? SessionId = null, AgentTurnRecord? Turn = null, string? ProfileId = null);

internal sealed class SubagentLocalManagementGateway(
    SubagentService service,
    IPackageExtensionCatalog extensionCatalog) : ISubagentManagementGateway, IDisposable
{
    private readonly SubagentEditorCapabilityCatalog _capabilities = new(extensionCatalog);
    public event Action? SubagentsChanged { add => service.SubagentsChanged += value; remove => service.SubagentsChanged -= value; }
    public event Action? CatalogChanged { add => _capabilities.Changed += value; remove => _capabilities.Changed -= value; }
    public Task<IReadOnlyList<SubagentRecord>> ListSubagentsAsync(CancellationToken cancellationToken = default) => Task.FromResult(service.ListSubagents());
    public Task<SubagentRecord> CreateSubagentAsync(string displayName, CancellationToken cancellationToken = default) => Task.FromResult(service.CreateSubagent(displayName));
    public Task<SubagentRecord> SaveSubagentAsync(SubagentSaveRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(service.SaveSubagent(request.SubagentId, request.DisplayName, request.Description, request.Instructions,
            request.ChatProviderId, request.ChatModelId, request.Assignments, request.ChatModelSettingsJson));
    public Task DeleteSubagentAsync(string subagentId, CancellationToken cancellationToken = default) { service.DeleteSubagent(subagentId); return Task.CompletedTask; }
    public IReadOnlyList<ProviderCatalogOption> ListChatProviders() => ProviderModelCatalogAdapter.ForChatProviders(extensionCatalog).ListProviders();
    public Task<ProviderModelCatalogResult> LoadChatModelsAsync(string providerId, CancellationToken cancellationToken = default)
        => ProviderModelCatalogAdapter.ForChatProviders(extensionCatalog).LoadAsync(providerId, cancellationToken);
    public Task<IReadOnlyList<AgentToolDescriptor>> ListLocalToolsAsync(CancellationToken cancellationToken = default) => _capabilities.ListLocalToolsAsync(cancellationToken);
    public Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListPackageCapabilitiesAsync(CancellationToken cancellationToken = default) => _capabilities.ListPackageCapabilitiesAsync(cancellationToken);
    public void Dispose() => _capabilities.Dispose();
}

internal sealed class SubagentAppRuntimeGateway :
    ISubagentManagementGateway,
    ISubsessionSessionReader,
    ISubsessionCheckpointReader,
    ISubsessionTranscriptPageReader,
    ISubsessionChangeNotifications,
    ISubagentPresentationInitialization,
    IDisposable
{
    private readonly IPackageRuntimeClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _runtimeSnapshotLock = new();
    private IReadOnlyList<ProviderCatalogOption>? _providers;
    private Task<SubagentProjection>? _runtimeSnapshot;
    private Task<SubagentProjection>? _abandonedRuntimeSnapshot;
    private Task? _observationTask;
    private int _disposed;

    public SubagentAppRuntimeGateway(IPackageRuntimeClient client)
    {
        _client = client;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_runtimeSnapshotLock)
        {
            return _observationTask ??= StartObservingChangesAsync();
        }
    }

    private async Task StartObservingChangesAsync()
    {
        await Task.Yield();
        _ = ObserveChangesAsync(_lifetime.Token);
    }

    public event Action? SubagentsChanged;
    public event Action? CatalogChanged;
    public event Action<Guid>? SessionChanged;
    public event Action<Guid, AgentTurnRecord>? TurnChanged;

    public async Task<IReadOnlyList<SubagentRecord>> ListSubagentsAsync(CancellationToken cancellationToken = default)
    {
        var projection = await InvokeAsync(new(SubagentQueryKind.Management), cancellationToken).ConfigureAwait(false);
        UpdateProviders(projection);
        return projection.Subagents ?? [];
    }
    public async Task<SubagentRecord> CreateSubagentAsync(string displayName, CancellationToken cancellationToken = default)
        => (await CommandAsync(new(SubagentCommandKind.Create, displayName), cancellationToken).ConfigureAwait(false)).Subagent
           ?? throw new InvalidOperationException("Runtime did not return the created subagent.");
    public async Task<SubagentRecord> SaveSubagentAsync(SubagentSaveRequest request, CancellationToken cancellationToken = default)
        => (await CommandAsync(new(SubagentCommandKind.Save, Save: request), cancellationToken).ConfigureAwait(false)).Subagent
           ?? throw new InvalidOperationException("Runtime did not return the saved subagent.");
    public async Task DeleteSubagentAsync(string subagentId, CancellationToken cancellationToken = default)
        => _ = await CommandAsync(new(SubagentCommandKind.Delete, subagentId), cancellationToken);

    public IReadOnlyList<ProviderCatalogOption> ListChatProviders()
        => _providers
           ?? throw new InvalidOperationException("Subagent management must initialize before providers are read.");

    public async Task<ProviderModelCatalogResult> LoadChatModelsAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var provider = (await InvokeAsync(new(SubagentQueryKind.ProviderModels, ProviderId: providerId), cancellationToken))
            .Providers?.FirstOrDefault();
        if (provider is null) return new([], "Chat provider is no longer installed.");
        var readiness = provider.Readiness;
        return new(
            (provider.Models ?? []).OrderNewestFirst().Select(ProviderModelCatalogAdapter.ToCatalogOption).ToArray(),
            readiness is null ? "Chat provider status is unavailable." : $"Chat provider status: {readiness.Status} - {readiness.Message}",
            readiness?.Status == AgentProviderReadinessStatus.Ready ? null : readiness?.Message);
    }

    public async Task<IReadOnlyList<AgentToolDescriptor>> ListLocalToolsAsync(CancellationToken cancellationToken = default)
        => (await InvokeAsync(new(SubagentQueryKind.Management), cancellationToken)).Tools ?? [];
    public async Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListPackageCapabilitiesAsync(CancellationToken cancellationToken = default)
        => (await InvokeAsync(new(SubagentQueryKind.Management), cancellationToken)).Capabilities ?? [];

    public async Task<SubsessionSessionCatalog> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var projection = await GetRuntimeSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return new SubsessionSessionCatalog(projection.Sessions ?? [], projection.Profiles ?? []);
    }

    public async Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(
        CancellationToken cancellationToken = default)
        => (await GetRuntimeSnapshotAsync(cancellationToken).ConfigureAwait(false)).Checkpoints ?? [];

    public async Task<IReadOnlyList<AgentTurnRecord>> ListRecentTurnsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(new(SubagentQueryKind.RecentTurns, SessionId: sessionId, Limit: limit), cancellationToken)
            .ConfigureAwait(false)).Turns ?? [];

    public async Task<IReadOnlyList<AgentTurnRecord>> ListTurnsBeforeAsync(
        Guid sessionId,
        DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId,
        int limit,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(new(SubagentQueryKind.TurnsBefore, SessionId: sessionId, Limit: limit,
            AnchorCreatedAtUtc: beforeCreatedAtUtc, AnchorTurnId: beforeTurnId), cancellationToken)
            .ConfigureAwait(false)).Turns ?? [];

    public async Task<IReadOnlyList<AgentTurnRecord>> ListTurnsAfterAsync(
        Guid sessionId,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId,
        int limit,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(new(SubagentQueryKind.TurnsAfter, SessionId: sessionId, Limit: limit,
            AnchorCreatedAtUtc: afterCreatedAtUtc, AnchorTurnId: afterTurnId), cancellationToken)
            .ConfigureAwait(false)).Turns ?? [];

    private async Task<SubagentProjection> GetRuntimeSnapshotAsync(CancellationToken cancellationToken)
    {
        Task<SubagentProjection> snapshot;
        bool retryIfFaulted;
        lock (_runtimeSnapshotLock)
        {
            if (_runtimeSnapshot is { IsFaulted: true } or { IsCanceled: true })
            {
                _ = _runtimeSnapshot.Exception;
                _runtimeSnapshot = null;
                _abandonedRuntimeSnapshot = null;
            }
            snapshot = _runtimeSnapshot ??= InvokeAsync(
                new(SubagentQueryKind.RuntimeCatalog),
                _lifetime.Token);
            retryIfFaulted = ReferenceEquals(_abandonedRuntimeSnapshot, snapshot);
        }

        try
        {
            var projection = await snapshot.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_runtimeSnapshotLock)
            {
                if (ReferenceEquals(_abandonedRuntimeSnapshot, snapshot))
                {
                    _abandonedRuntimeSnapshot = null;
                }
            }
            return projection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_runtimeSnapshotLock)
            {
                if (ReferenceEquals(_runtimeSnapshot, snapshot))
                {
                    _abandonedRuntimeSnapshot = snapshot;
                }
            }
            throw;
        }
        catch
        {
            lock (_runtimeSnapshotLock)
            {
                if (ReferenceEquals(_runtimeSnapshot, snapshot))
                {
                    _runtimeSnapshot = null;
                }
                if (ReferenceEquals(_abandonedRuntimeSnapshot, snapshot))
                {
                    _abandonedRuntimeSnapshot = null;
                }
            }
            if (retryIfFaulted)
            {
                return await GetRuntimeSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
    }

    private void InvalidateRuntimeSnapshot()
    {
        lock (_runtimeSnapshotLock)
        {
            _runtimeSnapshot = null;
            _abandonedRuntimeSnapshot = null;
        }
    }

    private async Task<SubagentProjection> InvokeAsync(SubagentQuery request, CancellationToken cancellationToken)
        => await _client.InvokeAsync(SubagentRuntimeOperations.Query, request, cancellationToken).ConfigureAwait(false);
    private async Task<SubagentProjection> CommandAsync(SubagentCommand request, CancellationToken cancellationToken)
        => await _client.InvokeAsync(SubagentRuntimeOperations.Command, request, cancellationToken).ConfigureAwait(false);

    private async Task ObserveChangesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in _client.SubscribeAsync(SubagentRuntimeOperations.Changes, new SubagentChangeSubscription(), cancellationToken))
                {
                    switch (change.Kind)
                    {
                        case SubagentChangeKind.Subagents: SubagentsChanged?.Invoke(); break;
                        case SubagentChangeKind.Catalog:
                            UpdateProviders(await InvokeAsync(
                                new(SubagentQueryKind.Management),
                                cancellationToken).ConfigureAwait(false));
                            CatalogChanged?.Invoke();
                            break;
                        case SubagentChangeKind.Session when change.SessionId is { } sessionId:
                            InvalidateRuntimeSnapshot();
                            SessionChanged?.Invoke(sessionId);
                            break;
                        case SubagentChangeKind.Turn when change.SessionId is { } sessionId && change.Turn is { } turn: TurnChanged?.Invoke(sessionId, turn); break;
                        case SubagentChangeKind.Profile:
                            InvalidateRuntimeSnapshot();
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch { }
            try { await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void UpdateProviders(SubagentProjection projection)
        => _providers = (projection.Providers ?? []).Select(provider =>
            new ProviderCatalogOption(provider.ProviderId, provider.DisplayName, provider.PackageId)).ToArray();
}

internal sealed class SubsessionLocalRuntimeAdapter(IAgentRuntimeCatalog runtime) :
    ISubsessionSessionReader,
    ISubsessionCheckpointReader,
    ISubsessionTranscriptPageReader,
    ISubsessionChangeNotifications
{
    public event Action<Guid>? SessionChanged
    {
        add => runtime.SessionChanged += value;
        remove => runtime.SessionChanged -= value;
    }

    public event Action<Guid, AgentTurnRecord>? TurnChanged
    {
        add => runtime.TurnChanged += value;
        remove => runtime.TurnChanged -= value;
    }

    public Task<SubsessionSessionCatalog> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SubsessionSessionCatalog(runtime.ListSessions(), runtime.ListProfiles()));
    }

    public Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AgentRunCheckpointRecord>>(runtime.ListSessions()
            .Select(session => runtime.GetLatestCheckpoint(session.SessionId))
            .OfType<AgentRunCheckpointRecord>()
            .ToArray());
    }

    public Task<IReadOnlyList<AgentTurnRecord>> ListRecentTurnsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(runtime.ListRecentTurns(sessionId, limit));
    }

    public Task<IReadOnlyList<AgentTurnRecord>> ListTurnsBeforeAsync(
        Guid sessionId,
        DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(runtime.ListTurnsBefore(sessionId, beforeCreatedAtUtc, beforeTurnId, limit));
    }

    public Task<IReadOnlyList<AgentTurnRecord>> ListTurnsAfterAsync(
        Guid sessionId,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(runtime.ListTurnsAfter(sessionId, afterCreatedAtUtc, afterTurnId, limit));
    }
}

internal sealed class SubagentRuntimeHandler(
    SubagentService service,
    IPackageExtensionCatalog extensions)
    : IPackageRuntimeOperationHandler<SubagentQuery, SubagentProjection>,
      IPackageRuntimeOperationHandler<SubagentCommand, SubagentProjection>
{
    private readonly SubagentEditorCapabilityCatalog _capabilities = new(extensions);

    public async ValueTask<SubagentProjection> HandleAsync(SubagentQuery request, CancellationToken cancellationToken = default)
    {
        var runtime = extensions.GetExtensions(PackageExtensionPoints.RuntimeCatalogs).FirstOrDefault();
        return request.Kind switch
        {
            SubagentQueryKind.Management => await ManagementAsync(cancellationToken),
            SubagentQueryKind.ProviderModels => await ProviderAsync(Require(request.ProviderId), cancellationToken),
            SubagentQueryKind.RuntimeCatalog => RuntimeProjection(RequireRuntime(runtime)),
            SubagentQueryKind.RecentTurns => new(Turns: RequireRuntime(runtime).ListRecentTurns(
                Require(request.SessionId), Math.Clamp(request.Limit, 1, 500))),
            SubagentQueryKind.TurnsBefore => new(Turns: RequireRuntime(runtime).ListTurnsBefore(
                Require(request.SessionId), Require(request.AnchorCreatedAtUtc), Require(request.AnchorTurnId),
                Math.Clamp(request.Limit, 1, 500))),
            SubagentQueryKind.TurnsAfter => new(Turns: RequireRuntime(runtime).ListTurnsAfter(
                Require(request.SessionId), Require(request.AnchorCreatedAtUtc), Require(request.AnchorTurnId),
                Math.Clamp(request.Limit, 1, 500))),
            _ => throw new InvalidOperationException("Unknown subagent query."),
        };
    }

    public ValueTask<SubagentProjection> HandleAsync(SubagentCommand request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = request.Kind switch
        {
            SubagentCommandKind.Create => service.CreateSubagent(request.Value ?? "New Subagent"),
            SubagentCommandKind.Save when request.Save is { } save => service.SaveSubagent(save.SubagentId, save.DisplayName,
                save.Description, save.Instructions, save.ChatProviderId, save.ChatModelId, save.Assignments, save.ChatModelSettingsJson),
            SubagentCommandKind.Delete => Delete(Require(request.Value)),
            _ => throw new InvalidOperationException("Unknown subagent command."),
        };
        return ValueTask.FromResult(new SubagentProjection(Subagents: service.ListSubagents(), Subagent: result));
    }

    private async Task<SubagentProjection> ManagementAsync(CancellationToken cancellationToken)
    {
        var providers = extensions.GetExtensionContributions(PackageExtensionPoints.ChatProviders)
            .OrderBy(item => item.Contribution.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(item => new SubagentProviderProjection(item.Contribution.Descriptor.ProviderId,
                item.Contribution.Descriptor.DisplayName, item.PackageId)).ToArray();
        var toolsTask = _capabilities.ListLocalToolsAsync(cancellationToken);
        var capabilitiesTask = _capabilities.ListPackageCapabilitiesAsync(cancellationToken);
        await Task.WhenAll(toolsTask, capabilitiesTask);
        return new(Subagents: service.ListSubagents(), Providers: providers,
            Tools: await toolsTask, Capabilities: await capabilitiesTask);
    }

    private async Task<SubagentProjection> ProviderAsync(string providerId, CancellationToken cancellationToken)
    {
        var contribution = extensions.GetExtensionContributions(PackageExtensionPoints.ChatProviders)
            .FirstOrDefault(item => string.Equals(item.Contribution.Descriptor.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (contribution is null) return new(Providers: []);
        var modelsTask = contribution.Contribution.GetAvailableModelsAsync(cancellationToken).AsTask();
        var readinessTask = contribution.Contribution.GetReadinessAsync(cancellationToken).AsTask();
        await Task.WhenAll(modelsTask, readinessTask);
        return new(Providers: [new(providerId, contribution.Contribution.Descriptor.DisplayName,
            contribution.PackageId, await modelsTask, await readinessTask)]);
    }

    private static SubagentProjection RuntimeProjection(IAgentRuntimeCatalog runtime)
    {
        var sessions = runtime.ListSessions();
        return new(Sessions: sessions, Profiles: runtime.ListProfiles(),
            Checkpoints: sessions.Select(session => runtime.GetLatestCheckpoint(session.SessionId)).Where(item => item is not null).Cast<AgentRunCheckpointRecord>().ToArray());
    }

    private SubagentRecord? Delete(string id) { service.DeleteSubagent(id); return null; }
    private static string Require(string? value) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException("A value is required.") : value;
    private static Guid Require(Guid? value) => value ?? throw new InvalidOperationException("A session id is required.");
    private static DateTimeOffset Require(DateTimeOffset? value) => value ?? throw new InvalidOperationException("A transcript anchor is required.");
    private static IAgentRuntimeCatalog RequireRuntime(IAgentRuntimeCatalog? runtime)
        => runtime ?? throw new InvalidOperationException("The Agent runtime catalog is not available.");
}

internal sealed class SubagentRuntimeChangeStream(SubagentService service, IPackageExtensionCatalog extensions)
    : IPackageRuntimeStreamHandler<SubagentChangeSubscription, SubagentChanged>
{
    public async IAsyncEnumerable<SubagentChanged> SubscribeAsync(SubagentChangeSubscription request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<SubagentChanged>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var runtime = extensions.GetExtensions(PackageExtensionPoints.RuntimeCatalogs).FirstOrDefault();
        void Subagents() => channel.Writer.TryWrite(new(SubagentChangeKind.Subagents));
        void Catalog() => channel.Writer.TryWrite(new(SubagentChangeKind.Catalog));
        void Session(Guid id) => channel.Writer.TryWrite(new(SubagentChangeKind.Session, id));
        void Turn(Guid id, AgentTurnRecord turn) => channel.Writer.TryWrite(new(SubagentChangeKind.Turn, id, turn));
        void Profile(string id) => channel.Writer.TryWrite(new(SubagentChangeKind.Profile, ProfileId: id));
        service.SubagentsChanged += Subagents;
        using var capabilities = new SubagentEditorCapabilityCatalog(extensions);
        capabilities.Changed += Catalog;
        if (runtime is not null) { runtime.SessionChanged += Session; runtime.TurnChanged += Turn; runtime.ProfileChanged += Profile; }
        try { await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken)) yield return change; }
        finally
        {
            service.SubagentsChanged -= Subagents;
            capabilities.Changed -= Catalog;
            if (runtime is not null) { runtime.SessionChanged -= Session; runtime.TurnChanged -= Turn; runtime.ProfileChanged -= Profile; }
            channel.Writer.TryComplete();
        }
    }
}
