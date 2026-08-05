using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Shared.PackageViews;
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
    Task<IReadOnlyList<ProviderCatalogOption>> ListChatProvidersAsync(
        CancellationToken cancellationToken = default);
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
    Task<SubsessionTranscriptPage> ListRecentTurnsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(
        Guid sessionId,
        DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<SubsessionTranscriptPage> ListTurnsAfterAsync(
        Guid sessionId,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<SubsessionAroundTurnPage> LoadAroundTurnAsync(
        Guid sessionId,
        Guid turnId,
        DateTimeOffset turnCreatedAtUtc,
        Guid itemId,
        int beforeLimit,
        int afterLimit,
        CancellationToken cancellationToken = default);

    Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult<AgentTranscriptToolDetailRecord?>(null);
}

internal interface ISubsessionChangeNotifications
{
    event Action<Guid>? SessionChanged;
    event Action<Guid, AgentTurnRecord>? TurnChanged;
    event Action? ResnapshotRequired;
}

internal interface ISubagentPresentationInitialization
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

internal sealed record SubsessionSessionCatalog(
    IReadOnlyList<AgentSessionRecord> Sessions,
    IReadOnlyList<AgentProfileRecord> Profiles);

internal sealed record SubsessionAroundTurnPage(
    IReadOnlyList<AgentTurnRecord> Turns,
    bool HasOlder,
    bool HasNewer,
    Guid AnchorTurnId);

internal sealed record SubsessionTranscriptPage(
    IReadOnlyList<AgentTurnRecord> Turns,
    bool HasMore,
    TranscriptPageCursor? Continuation = null);

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

internal enum SubagentQueryKind { Management, ProviderModels, RuntimeCatalog, RecentTurns, TurnsBefore, TurnsAfter, AroundTurn, ToolDetail }
internal sealed record SubagentQuery(
    SubagentQueryKind Kind,
    string? ProviderId = null,
    Guid? SessionId = null,
    int Limit = 100,
    DateTimeOffset? AnchorCreatedAtUtc = null,
    Guid? AnchorTurnId = null,
    Guid? ItemId = null,
    int AfterLimit = 0,
    AgentTranscriptToolDetailRequest? ToolDetailRequest = null);

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
    SubsessionTranscriptPage? TranscriptPage = null,
    SubsessionAroundTurnPage? AroundTurn = null,
    AgentTranscriptToolDetailRecord? ToolDetail = null);

internal sealed record SubagentChangeSubscription(long AfterRevision = 0);
internal enum SubagentChangeKind
{
    Connected,
    ResnapshotRequired,
    Subagents,
    Catalog,
    Session,
    Turn,
    Profile,
}
internal sealed record SubagentChanged(
    long Revision,
    SubagentChangeKind Kind,
    Guid? SessionId = null,
    AgentTurnRecord? Turn = null,
    string? ProfileId = null,
    string? RuntimeInstanceId = null);

internal sealed class SubagentLocalManagementGateway(
    SubagentService service,
    AgentRpcCatalog rpcCatalog) : ISubagentManagementGateway, IDisposable
{
    private readonly SubagentEditorCapabilityCatalog _capabilities = new(rpcCatalog);
    public event Action? SubagentsChanged { add => service.SubagentsChanged += value; remove => service.SubagentsChanged -= value; }
    public event Action? CatalogChanged { add => _capabilities.Changed += value; remove => _capabilities.Changed -= value; }
    public Task<IReadOnlyList<SubagentRecord>> ListSubagentsAsync(CancellationToken cancellationToken = default) => Task.FromResult(service.ListSubagents());
    public Task<SubagentRecord> CreateSubagentAsync(string displayName, CancellationToken cancellationToken = default) => Task.FromResult(service.CreateSubagent(displayName));
    public Task<SubagentRecord> SaveSubagentAsync(SubagentSaveRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(service.SaveSubagent(request.SubagentId, request.DisplayName, request.Description, request.Instructions,
            request.ChatProviderId, request.ChatModelId, request.Assignments, request.ChatModelSettingsJson));
    public Task DeleteSubagentAsync(string subagentId, CancellationToken cancellationToken = default) { service.DeleteSubagent(subagentId); return Task.CompletedTask; }
    public Task<IReadOnlyList<ProviderCatalogOption>> ListChatProvidersAsync(
        CancellationToken cancellationToken = default)
        => ProviderModelCatalogAdapter.ForChatProviders(rpcCatalog)
            .ListProvidersAsync(cancellationToken);
    public Task<ProviderModelCatalogResult> LoadChatModelsAsync(string providerId, CancellationToken cancellationToken = default)
        => ProviderModelCatalogAdapter.ForChatProviders(rpcCatalog).LoadAsync(providerId, cancellationToken);
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
    private long _changeRevision;
    private string? _changeRuntimeInstanceId;
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
    public event Action? ResnapshotRequired;

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

    public Task<IReadOnlyList<ProviderCatalogOption>> ListChatProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_providers
            ?? throw new InvalidOperationException(
                "Subagent management must initialize before providers are read."));
    }

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

    public async Task<SubsessionTranscriptPage> ListRecentTurnsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(new(SubagentQueryKind.RecentTurns, SessionId: sessionId, Limit: limit), cancellationToken)
            .ConfigureAwait(false)).TranscriptPage
           ?? throw new InvalidOperationException("Runtime did not return the recent subsession transcript page.");

    public async Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(
        Guid sessionId,
        DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId,
        int limit,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(new(SubagentQueryKind.TurnsBefore, SessionId: sessionId, Limit: limit,
            AnchorCreatedAtUtc: beforeCreatedAtUtc, AnchorTurnId: beforeTurnId), cancellationToken)
            .ConfigureAwait(false)).TranscriptPage
           ?? throw new InvalidOperationException("Runtime did not return the older subsession transcript page.");

    public async Task<SubsessionTranscriptPage> ListTurnsAfterAsync(
        Guid sessionId,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId,
        int limit,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(new(SubagentQueryKind.TurnsAfter, SessionId: sessionId, Limit: limit,
            AnchorCreatedAtUtc: afterCreatedAtUtc, AnchorTurnId: afterTurnId), cancellationToken)
            .ConfigureAwait(false)).TranscriptPage
           ?? throw new InvalidOperationException("Runtime did not return the newer subsession transcript page.");

    public async Task<SubsessionAroundTurnPage> LoadAroundTurnAsync(
        Guid sessionId,
        Guid turnId,
        DateTimeOffset turnCreatedAtUtc,
        Guid itemId,
        int beforeLimit,
        int afterLimit,
        CancellationToken cancellationToken = default)
    {
        var projection = await InvokeAsync(new SubagentQuery(
            SubagentQueryKind.AroundTurn,
            SessionId: sessionId,
            Limit: Math.Clamp(beforeLimit, 0, 30),
            AnchorTurnId: turnId,
            AnchorCreatedAtUtc: turnCreatedAtUtc,
            ItemId: itemId,
            AfterLimit: Math.Clamp(afterLimit, 0, 30)), cancellationToken).ConfigureAwait(false);
        return projection.AroundTurn
               ?? throw new InvalidOperationException("Runtime did not return the requested transcript anchor.");
    }

    public async Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken = default)
        => (await InvokeAsync(
                new SubagentQuery(SubagentQueryKind.ToolDetail, ToolDetailRequest: request),
                cancellationToken)
            .ConfigureAwait(false)).ToolDetail;

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
                await foreach (var change in _client.SubscribeAsync(
                                   SubagentRuntimeOperations.Changes,
                                   new SubagentChangeSubscription(Interlocked.Read(ref _changeRevision)),
                                   cancellationToken))
                {
                    if (change.Kind == SubagentChangeKind.Connected)
                    {
                        var previousInstanceId = _changeRuntimeInstanceId;
                        _changeRuntimeInstanceId = change.RuntimeInstanceId;
                        Interlocked.Exchange(ref _changeRevision, change.Revision);
                        if (previousInstanceId is not null
                            && !string.Equals(
                                previousInstanceId,
                                change.RuntimeInstanceId,
                                StringComparison.Ordinal))
                        {
                            RequireResnapshot();
                        }
                        continue;
                    }
                    if (change.Kind == SubagentChangeKind.ResnapshotRequired)
                    {
                        _changeRuntimeInstanceId = change.RuntimeInstanceId ?? _changeRuntimeInstanceId;
                        Interlocked.Exchange(ref _changeRevision, change.Revision);
                        RequireResnapshot();
                        continue;
                    }

                    var previousRevision = Interlocked.Read(ref _changeRevision);
                    if (change.Revision <= previousRevision)
                    {
                        continue;
                    }
                    if (previousRevision > 0 && change.Revision != previousRevision + 1)
                    {
                        Interlocked.Exchange(ref _changeRevision, change.Revision);
                        RequireResnapshot();
                        continue;
                    }
                    Interlocked.Exchange(ref _changeRevision, change.Revision);
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

    private void RequireResnapshot()
    {
        InvalidateRuntimeSnapshot();
        ResnapshotRequired?.Invoke();
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

internal sealed class SubagentRuntimeHandler(
    SubagentService service,
    AgentRpcCatalog rpcCatalog)
    : IPackageRuntimeOperationHandler<SubagentQuery, SubagentProjection>,
      IPackageRuntimeOperationHandler<SubagentCommand, SubagentProjection>
{
    private readonly SubagentEditorCapabilityCatalog _capabilities = new(rpcCatalog);

    public async ValueTask<SubagentProjection> HandleAsync(SubagentQuery request, CancellationToken cancellationToken = default)
    {
        if (request.Kind == SubagentQueryKind.Management)
        {
            return await ManagementAsync(cancellationToken);
        }
        if (request.Kind == SubagentQueryKind.ProviderModels)
        {
            return await ProviderAsync(Require(request.ProviderId), cancellationToken);
        }

        return await InvokeRuntimeAsync(
            runtime => request.Kind switch
            {
                SubagentQueryKind.RuntimeCatalog => RuntimeProjection(runtime),
                SubagentQueryKind.RecentTurns => new(TranscriptPage: ReadTranscriptPage(runtime, request)),
                SubagentQueryKind.TurnsBefore => new(TranscriptPage: ReadTranscriptPage(runtime, request)),
                SubagentQueryKind.TurnsAfter => new(TranscriptPage: ReadTranscriptPage(runtime, request)),
                SubagentQueryKind.AroundTurn => new(AroundTurn: SubsessionLocalRuntimeAdapter.BuildAroundTurnPage(
                    runtime, Require(request.SessionId), Require(request.AnchorTurnId),
                    Require(request.AnchorCreatedAtUtc),
                    Require(request.ItemId),
                    Math.Clamp(request.Limit, 0, 30), Math.Clamp(request.AfterLimit, 0, 30))),
                SubagentQueryKind.ToolDetail => new(ToolDetail: SubsessionLocalRuntimeAdapter.GetTranscriptToolDetail(
                        runtime,
                        request.ToolDetailRequest
                        ?? throw new InvalidOperationException("A tool detail request is required.")) is { } detail
                    ? SubsessionAroundTurnPayload.FitToolDetail(detail)
                    : null),
                _ => throw new InvalidOperationException("Unknown subagent query."),
            },
            cancellationToken);
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
        var providers = SnapshotChatProviders(cancellationToken)
            .OrderBy(item => item.Metadata.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(item => new SubagentProviderProjection(item.Metadata.ProviderId,
                item.Metadata.DisplayName, item.PackageId)).ToArray();
        var toolsTask = _capabilities.ListLocalToolsAsync(cancellationToken);
        var capabilitiesTask = _capabilities.ListPackageCapabilitiesAsync(cancellationToken);
        await Task.WhenAll(toolsTask, capabilitiesTask);
        return new(Subagents: service.ListSubagents(), Providers: providers,
            Tools: await toolsTask, Capabilities: await capabilitiesTask);
    }

    private async Task<SubagentProjection> ProviderAsync(string providerId, CancellationToken cancellationToken)
    {
        var contribution = SnapshotChatProviders(cancellationToken)
            .FirstOrDefault(item => string.Equals(item.Metadata.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (contribution is null) return new(Providers: []);
        var models = await SubagentCatalogRpcInvocation.InvokeOptionalAsync(
            contribution,
            cancellationToken,
            static (provider, token) => provider.GetAvailableModelsAsync(token)).ConfigureAwait(false);
        var readiness = await SubagentCatalogRpcInvocation.InvokeOptionalAsync(
            contribution,
            cancellationToken,
            static (provider, token) => provider.GetReadinessAsync(token)).ConfigureAwait(false);
        if (!models.IsAvailable || !readiness.IsAvailable)
        {
            return new(Providers: [new(
                contribution.Metadata.ProviderId,
                contribution.Metadata.DisplayName,
                contribution.PackageId,
                Models: models.IsAvailable ? models.Value : [],
                Readiness: new AgentProviderReadiness(
                    contribution.Metadata.ProviderId,
                    AgentProviderReadinessStatus.Failed,
                    "The chat provider package is no longer available."))]);
        }

        return new(Providers: [new(
            contribution.Metadata.ProviderId,
            contribution.Metadata.DisplayName,
            contribution.PackageId,
            models.Value,
            readiness.Value)]);
    }

    private IReadOnlyList<SubagentCatalogRpcReference<IAgentChatProvider, AgentProviderDescriptor>>
        SnapshotChatProviders(CancellationToken cancellationToken)
        => SubagentCatalogRpcInvocation.Snapshot(
            rpcCatalog,
            AgentRpcServices.ChatProviders,
            cancellationToken,
            static provider => provider.Descriptor with
            {
                SupportedAuthModes = provider.Descriptor.SupportedAuthModes.ToArray(),
            });

    private async ValueTask<SubagentProjection> InvokeRuntimeAsync(
        Func<IAgentRuntimeCatalog, SubagentProjection> callback,
        CancellationToken cancellationToken)
    {
        var reference = rpcCatalog.GetServiceReferences(AgentRpcServices.RuntimeCatalogs)
            .FirstOrDefault();
        if (reference is null || !reference.TryAcquire(out var lease))
        {
            throw new InvalidOperationException("The Agent runtime catalog is not available.");
        }

        using (lease)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Task.Run(() => callback(lease.Service), cancellationToken);
            if (lease.RetirementToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("The Agent runtime catalog became unavailable.");
            }
            return result;
        }
    }

    private static SubagentProjection RuntimeProjection(IAgentRuntimeCatalog runtime)
    {
        var sessions = runtime.ListSessions();
        return new(Sessions: sessions, Profiles: runtime.ListProfiles(),
            Checkpoints: sessions.Select(session => runtime.GetLatestCheckpoint(session.SessionId)).Where(item => item is not null).Cast<AgentRunCheckpointRecord>().ToArray());
    }

    private static SubsessionTranscriptPage ReadTranscriptPage(
        IAgentRuntimeCatalog runtime,
        SubagentQuery request)
    {
        var limit = Math.Clamp(request.Limit, 1, 500);
        var turns = request.Kind switch
        {
            SubagentQueryKind.RecentTurns => SubsessionLocalRuntimeAdapter.ListRecentTranscriptHeaders(
                runtime,
                Require(request.SessionId),
                limit + 1),
            SubagentQueryKind.TurnsBefore => SubsessionLocalRuntimeAdapter.ListTranscriptHeadersBefore(
                runtime,
                Require(request.SessionId),
                Require(request.AnchorCreatedAtUtc),
                Require(request.AnchorTurnId),
                limit + 1),
            SubagentQueryKind.TurnsAfter => SubsessionLocalRuntimeAdapter.ListTranscriptHeadersAfter(
                runtime,
                Require(request.SessionId),
                Require(request.AnchorCreatedAtUtc),
                Require(request.AnchorTurnId),
                limit + 1),
            _ => throw new InvalidOperationException("Unknown subsession transcript page direction."),
        };
        return SubsessionLocalRuntimeAdapter.BuildTranscriptPage(turns, limit, request.Kind);
    }

    private SubagentRecord? Delete(string id) { service.DeleteSubagent(id); return null; }
    private static string Require(string? value) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException("A value is required.") : value;
    private static Guid Require(Guid? value) => value ?? throw new InvalidOperationException("A session id is required.");
    private static DateTimeOffset Require(DateTimeOffset? value) => value ?? throw new InvalidOperationException("A transcript anchor is required.");
}
