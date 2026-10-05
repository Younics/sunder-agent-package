using System.Globalization;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.ImportApis.CSunder_Agent_Runtime_Catalog;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Runtime_Catalog;

namespace Sunder.Package.Agent.Memory.Semantic.Rpc;

internal sealed class GeneratedRuntimeCatalogClient : IAgentRuntimeCatalog, IDisposable, IAsyncDisposable
{
    private readonly ISunderRpcClient _rpc;
    private readonly Api.Contract _contract;
    private readonly Api.Provider _provider;
    private readonly CancellationTokenSource _lifetime;
    private readonly Task _watch;
    private int _disposed;

    internal GeneratedRuntimeCatalogClient(ISunderRpcClient rpc, Api.Contract contract, Api.Provider provider, CancellationToken retirement)
    {
        _rpc = rpc; _contract = contract; _provider = provider;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(retirement);
        _watch = Task.Run(WatchAsync);
    }

    public event Action<Guid>? SessionChanged;
    public event Action<Guid, AgentTurnRecord>? TurnChanged;
    public event Action<string>? ProfileChanged;
    public event Action? ResetRequired;

    public IReadOnlyList<AgentSessionRecord> ListSessions() => Invoke(async (client, token) => Map<AgentSessionRecord[]>(await client.ListSessionsAsync(new(), token).ConfigureAwait(false)));
    public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId) => Invoke(async (client, token) => Map<AgentSessionRecord[]>(await client.ListSessionsForProfileAsync(new() { Value = profileId }, token).ConfigureAwait(false)));
    public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId) => Invoke(async (client, token) => Map<AgentSessionRecord[]>(await client.ListSessionsForWorkspaceAsync(new() { Value = workspaceId }, token).ConfigureAwait(false)));
    public AgentSessionRecord? GetSession(Guid sessionId) => Invoke(async (client, token) => Optional<AgentSessionRecord>(await client.GetSessionAsync(new() { SessionId = sessionId }, token).ConfigureAwait(false)));
    public ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(CancellationToken cancellationToken = default)
        => InvokeAsync<IReadOnlyList<AgentWorkspaceRecord>>(async (client, token) => Map<AgentWorkspaceRecord[]>(await client.ListWorkspacesAsync(new(), token).ConfigureAwait(false)), cancellationToken);
    public ValueTask<AgentWorkspaceRecord?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
        => InvokeAsync(async (client, token) => Optional<AgentWorkspaceRecord>(await client.GetWorkspaceAsync(new() { Value = workspaceId }, token).ConfigureAwait(false)), cancellationToken);
    public AgentProfileRecord? GetSessionProfile(Guid sessionId) => Invoke(async (client, token) => Optional<AgentProfileRecord>(await client.GetSessionProfileAsync(new() { SessionId = sessionId }, token).ConfigureAwait(false)));
    public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => Invoke(async (client, token) => Optional<AgentWorkingSummaryRecord>(await client.GetWorkingSummaryAsync(new() { SessionId = sessionId }, token).ConfigureAwait(false)));
    public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId) => Invoke(async (client, token) => Optional<AgentSessionContextCheckpointRecord>(await client.GetSessionContextCheckpointAsync(new() { SessionId = sessionId }, token).ConfigureAwait(false)));
    public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => Invoke(async (client, token) => Optional<AgentRunCheckpointRecord>(await client.GetLatestCheckpointAsync(new() { SessionId = sessionId }, token).ConfigureAwait(false)));
    public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => Invoke(async (client, token) => Map<AgentTurnRecord[]>(await client.ListRecentTurnsAsync(new() { SessionId = sessionId, Limit = limit }, token).ConfigureAwait(false)));
    public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit)
        => Invoke(async (client, token) => Map<AgentTurnRecord[]>(await client.ListTurnsBeforeAsync(new() { SessionId = sessionId, CreatedAtUtc = Timestamp(beforeCreatedAtUtc), TurnId = beforeTurnId, Limit = limit }, token).ConfigureAwait(false)));
    public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit)
        => Invoke(async (client, token) => Map<AgentTurnRecord[]>(await client.ListTurnsAfterAsync(new() { SessionId = sessionId, CreatedAtUtc = Timestamp(afterCreatedAtUtc), TurnId = afterTurnId, Limit = limit }, token).ConfigureAwait(false)));
    public IReadOnlyList<AgentProfileRecord> ListProfiles() => Invoke(async (client, token) => Map<AgentProfileRecord[]>(await client.ListProfilesAsync(new(), token).ConfigureAwait(false)));
    public AgentProfileRecord? GetProfile(string profileId) => Invoke(async (client, token) => Optional<AgentProfileRecord>(await client.GetProfileAsync(new() { Value = profileId }, token).ConfigureAwait(false)));
    public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind)
        => Invoke(async (client, token) => Optional<AgentProfileModelBindingRecord>(await client.GetSessionModelBindingAsync(new() { SessionId = sessionId, CapabilityKind = capabilityKind }, token).ConfigureAwait(false)));
    public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind)
        => Invoke(async (client, token) => Optional<AgentProfileModelBindingRecord>(await client.GetModelBindingAsync(new() { ProfileId = profileId, CapabilityKind = capabilityKind }, token).ConfigureAwait(false)));

    private T Invoke<T>(Func<Wire.RuntimeCatalogClient, CancellationToken, ValueTask<T>> callback) => InvokeAsync(callback, default).AsTask().GetAwaiter().GetResult();
    private async ValueTask<T> InvokeAsync<T>(Func<Wire.RuntimeCatalogClient, CancellationToken, ValueTask<T>> callback, CancellationToken token)
    {
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await using var scope = await _rpc.CreateCallScopeAsync(cancellationToken: invocation.Token).ConfigureAwait(false);
        return await callback(_contract.CreateClient(scope, _provider).SRuntime_2dcatalog, invocation.Token).ConfigureAwait(false);
    }

    private async Task WatchAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await using var scope = await _rpc.CreateCallScopeAsync(cancellationToken: _lifetime.Token).ConfigureAwait(false);
                var expected = 1L;
                await foreach (var item in _contract.CreateClient(scope, _provider).SRuntime_2dcatalog.WatchAsync(new(), _lifetime.Token).ConfigureAwait(false))
                {
                    var sequence = item switch
                    {
                        Wire.AgentRuntimeCatalogResetEvent value => value.Sequence,
                        Wire.AgentSessionCatalogEvent value => value.Sequence,
                        Wire.AgentTurnCatalogEvent value => value.Sequence,
                        Wire.AgentProfileCatalogEvent value => value.Sequence,
                        _ => throw new InvalidDataException("Unknown Runtime catalog event."),
                    };
                    if (sequence != expected && item is not Wire.AgentRuntimeCatalogResetEvent) ResetRequired?.Invoke();
                    expected = sequence + 1;
                    switch (item)
                    {
                        case Wire.AgentRuntimeCatalogResetEvent: ResetRequired?.Invoke(); break;
                        case Wire.AgentSessionCatalogEvent value: SessionChanged?.Invoke(value.SessionId); break;
                        case Wire.AgentTurnCatalogEvent value: TurnChanged?.Invoke(value.SessionId, Map<AgentTurnRecord>(value.Turn)); break;
                        case Wire.AgentProfileCatalogEvent value: ProfileChanged?.Invoke(value.ProfileId); break;
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (error is OperationCanceledException or SunderRpcException) { ResetRequired?.Invoke(); }
            try { await Task.Delay(250, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _lifetime.Cancel();
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        try { await _watch.ConfigureAwait(false); }
        finally { _lifetime.Dispose(); }
    }
    private static T Map<T>(object value) where T : class => SemanticProviderValues.Convert<T>(value);
    private static T? Optional<T>(object? value) where T : class => value is null ? null : Map<T>(value);
    private static SunderRpcOffsetDateTime Timestamp(DateTimeOffset value) => new(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
}
