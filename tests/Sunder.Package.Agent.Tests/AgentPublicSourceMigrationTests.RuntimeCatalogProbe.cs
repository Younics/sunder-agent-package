using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private sealed class RuntimeCatalogProbe : IRuntimeCatalogService, IDisposable
    {
        private readonly IRuntimeCatalogService _inner;
        private readonly Action _unsubscribe;
        private readonly bool _fullTranscript;
        internal GeneratedServiceTestScope? ProjectionReceiver { get; set; }
        internal object CanonicalOwner => _inner is RuntimeCatalogProbe probe ? probe.CanonicalOwner : AgentRoleBoundary.Unwrap(_inner);

        public RuntimeCatalogProbe(AgentRuntimeCatalog inner, AgentSessionService sessions, AgentProfileService profiles)
        {
            _inner = AgentRoleBoundary.As<IRuntimeCatalogService>(inner);
            sessions.SessionChanged += RaiseSession;
            sessions.TurnChanged += RelayTurn;
            profiles.ProfileChanged += RaiseProfile;
            _unsubscribe = () =>
            {
                sessions.SessionChanged -= RaiseSession;
                sessions.TurnChanged -= RelayTurn;
                profiles.ProfileChanged -= RaiseProfile;
            };
        }

        public RuntimeCatalogProbe(RuntimeCatalogProbe inner)
        {
            _inner = inner;
            _fullTranscript = true;
            inner.SessionChanged += RaiseSession;
            inner.TurnChanged += RelayTurn;
            inner.ProfileChanged += RaiseProfile;
            inner.ResetRequired += RaiseReset;
            _unsubscribe = () =>
            {
                inner.SessionChanged -= RaiseSession;
                inner.TurnChanged -= RelayTurn;
                inner.ProfileChanged -= RaiseProfile;
                inner.ResetRequired -= RaiseReset;
            };
        }

        public void Dispose() => _unsubscribe();
        private void RelayTurn(Guid sessionId, AgentTurnRecord turn) => TurnChanged?.Invoke(sessionId, turn);
        public event Action<Guid>? SessionChanged;
        public event Action<Guid, AgentTurnRecord>? TurnChanged;
        public event Action<string>? ProfileChanged;
        public event Action? ResetRequired;
        public int SubscriberCount => (SessionChanged?.GetInvocationList().Length ?? 0) + (TurnChanged?.GetInvocationList().Length ?? 0)
            + (ProfileChanged?.GetInvocationList().Length ?? 0) + (ResetRequired?.GetInvocationList().Length ?? 0);
        public bool Fail { get; set; }
        public int ProviderCalls { get; private set; }
        public bool BlockWorkspaces { get; set; }
        public TaskCompletionSource Started { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void RaiseSession(Guid id) => SessionChanged?.Invoke(id);
        public void RaiseTurn(AgentTurnRecord turn) => TurnChanged?.Invoke(turn.SessionId, turn);
        public void RaiseProfile(string id) => ProfileChanged?.Invoke(id);
        public void RaiseReset() => ResetRequired?.Invoke();
        public ValueTask<IReadOnlyList<AgentSessionRecord>> ListSessionsAsync(CancellationToken token = default)
            => Fail ? ValueTask.FromException<IReadOnlyList<AgentSessionRecord>>(new IOException("private catalog storage detail")) : _inner.ListSessionsAsync(token);
        public ValueTask<IReadOnlyList<AgentSessionRecord>> ListSessionsForProfileAsync(string profileId, CancellationToken token = default)
            => _inner.ListSessionsForProfileAsync(profileId, token);
        public ValueTask<IReadOnlyList<AgentSessionRecord>> ListSessionsForWorkspaceAsync(string workspaceId, CancellationToken token = default)
            => _inner.ListSessionsForWorkspaceAsync(workspaceId, token);
        public ValueTask<AgentSessionRecord?> GetSessionAsync(Guid sessionId, CancellationToken token = default) => _inner.GetSessionAsync(sessionId, token);
        public async ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(CancellationToken cancellationToken = default)
        {
            if (BlockWorkspaces) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return await _inner.ListWorkspacesAsync(cancellationToken);
        }
        public async ValueTask<AgentWorkspaceRecord?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
        {
            if (BlockWorkspaces) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return await _inner.GetWorkspaceAsync(workspaceId, cancellationToken);
        }
        public ValueTask<AgentProfileRecord?> GetSessionProfileAsync(Guid sessionId, CancellationToken token = default) => _inner.GetSessionProfileAsync(sessionId, token);
        public ValueTask<AgentWorkingSummaryRecord?> GetWorkingSummaryAsync(Guid sessionId, CancellationToken token = default) => _inner.GetWorkingSummaryAsync(sessionId, token);
        public ValueTask<AgentSessionContextCheckpointRecord?> GetSessionContextCheckpointAsync(Guid sessionId, CancellationToken token = default) => _inner.GetSessionContextCheckpointAsync(sessionId, token);
        public ValueTask<AgentRunCheckpointRecord?> GetLatestCheckpointAsync(Guid sessionId, CancellationToken token = default) => _inner.GetLatestCheckpointAsync(sessionId, token);
        public ValueTask<IReadOnlyList<AgentTurnRecord>> ListRecentTurnsAsync(Guid sessionId, int limit, CancellationToken token = default) => _inner.ListRecentTurnsAsync(sessionId, limit, token);
        public ValueTask<IReadOnlyList<AgentTurnRecord>> ListTurnsBeforeAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken token = default)
            => _inner.ListTurnsBeforeAsync(sessionId, createdAtUtc, turnId, limit, token);
        public ValueTask<IReadOnlyList<AgentTurnRecord>> ListTurnsAfterAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken token = default)
            => _inner.ListTurnsAfterAsync(sessionId, createdAtUtc, turnId, limit, token);
        public ValueTask<IReadOnlyList<AgentTurnRecord>> ListRecentTranscriptHeadersAsync(Guid sessionId, int limit, CancellationToken token = default)
            => _fullTranscript ? _inner.ListRecentTurnsAsync(sessionId, limit, token) : _inner.ListRecentTranscriptHeadersAsync(sessionId, limit, token);
        public ValueTask<IReadOnlyList<AgentTurnRecord>> ListTranscriptHeadersBeforeAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken token = default)
            => _fullTranscript ? _inner.ListTurnsBeforeAsync(sessionId, createdAtUtc, turnId, limit, token) : _inner.ListTranscriptHeadersBeforeAsync(sessionId, createdAtUtc, turnId, limit, token);
        public ValueTask<IReadOnlyList<AgentTurnRecord>> ListTranscriptHeadersAfterAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken token = default)
            => _fullTranscript ? _inner.ListTurnsAfterAsync(sessionId, createdAtUtc, turnId, limit, token) : _inner.ListTranscriptHeadersAfterAsync(sessionId, createdAtUtc, turnId, limit, token);
        public ValueTask<AgentTranscriptToolDetailRecord?> GetTranscriptToolDetailAsync(AgentTranscriptToolDetailRequest request, CancellationToken token = default)
            => _fullTranscript ? ValueTask.FromResult<AgentTranscriptToolDetailRecord?>(null) : _inner.GetTranscriptToolDetailAsync(request, token);
        public ValueTask<IReadOnlyList<AgentProfileRecord>> ListProfilesAsync(CancellationToken token = default) => _inner.ListProfilesAsync(token);
        public ValueTask<AgentProfileRecord?> GetProfileAsync(string profileId, CancellationToken token = default) => _inner.GetProfileAsync(profileId, token);
        public ValueTask<AgentProfileModelBindingRecord?> GetSessionModelBindingAsync(Guid sessionId, string capabilityKind, CancellationToken token = default)
            => _inner.GetSessionModelBindingAsync(sessionId, capabilityKind, token);
        public ValueTask<AgentProfileModelBindingRecord?> GetModelBindingAsync(string profileId, string capabilityKind, CancellationToken token = default)
            => _inner.GetModelBindingAsync(profileId, capabilityKind, token);

        public object Invoke(string method, Type resultType, object? request, CancellationToken token)
        {
            ProviderCalls++;
            if (method == "WatchAsync")
                return typeof(RuntimeCatalogProbe).GetMethod(nameof(Watch), BindingFlags.Instance | BindingFlags.NonPublic)!
                    .MakeGenericMethod(resultType).Invoke(this, [token])!;
            return typeof(RuntimeCatalogProbe).GetMethod(nameof(Reply), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(resultType).Invoke(this, [method, JsonSerializer.SerializeToElement(request, ExecutionJson), token])!;
        }

        private async ValueTask<T> Reply<T>(string method, JsonElement request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string Text(string name) => request.ValueKind == JsonValueKind.String ? request.GetString()! : request.GetProperty(name).GetString()!;
            Guid Id(string name) => request.ValueKind == JsonValueKind.String ? request.GetGuid() : request.GetProperty(name).GetGuid();
            int Number(string name) => request.GetProperty(name).GetInt32();
            DateTimeOffset Time(string name)
                => new SunderRpcOffsetDateTime(request.GetProperty(name).GetString()!).TryGetDateTimeOffset(out var value)
                    ? value : throw new JsonException("The fixture's natural DateTimeOffset cannot represent the exact timestamp.");
            object? value = method switch
            {
                "ListSessionsAsync" => await ListSessionsAsync(token),
                "ListSessionsForProfileAsync" => await ListSessionsForProfileAsync(Text("profileId"), token),
                "ListSessionsForWorkspaceAsync" => await ListSessionsForWorkspaceAsync(Text("workspaceId"), token),
                "GetSessionAsync" => await GetSessionAsync(Id("sessionId"), token),
                "ListWorkspacesAsync" => await ListWorkspacesAsync(token),
                "GetWorkspaceAsync" => await GetWorkspaceAsync(Text("workspaceId"), token),
                "GetSessionProfileAsync" => await GetSessionProfileAsync(Id("sessionId"), token),
                "GetWorkingSummaryAsync" => await GetWorkingSummaryAsync(Id("sessionId"), token),
                "GetSessionContextCheckpointAsync" => await GetSessionContextCheckpointAsync(Id("sessionId"), token),
                "GetLatestCheckpointAsync" => await GetLatestCheckpointAsync(Id("sessionId"), token),
                "ListRecentTurnsAsync" => await ListRecentTurnsAsync(Id("sessionId"), Number("limit"), token),
                "ListTurnsBeforeAsync" => await ListTurnsBeforeAsync(Id("sessionId"), Time("createdAtUtc"), Id("turnId"), Number("limit"), token),
                "ListTurnsAfterAsync" => await ListTurnsAfterAsync(Id("sessionId"), Time("createdAtUtc"), Id("turnId"), Number("limit"), token),
                "ListRecentTranscriptHeadersAsync" => await ListRecentTranscriptHeadersAsync(Id("sessionId"), Number("limit"), token),
                "ListTranscriptHeadersBeforeAsync" => await ListTranscriptHeadersBeforeAsync(Id("sessionId"), Time("createdAtUtc"), Id("turnId"), Number("limit"), token),
                "ListTranscriptHeadersAfterAsync" => await ListTranscriptHeadersAfterAsync(Id("sessionId"), Time("createdAtUtc"), Id("turnId"), Number("limit"), token),
                "GetTranscriptToolDetailAsync" => await GetTranscriptToolDetailAsync(request.Deserialize<AgentTranscriptToolDetailRequest>(ExecutionJson)!, token),
                "ListProfilesAsync" => await ListProfilesAsync(token),
                "GetProfileAsync" => await GetProfileAsync(Text("profileId"), token),
                "GetSessionModelBindingAsync" => await GetSessionModelBindingAsync(Id("sessionId"), Text("capabilityKind"), token),
                "GetModelBindingAsync" => await GetModelBindingAsync(Text("profileId"), Text("capabilityKind"), token),
                _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown runtime catalog method."),
            };
            return await ProjectAsync<T>(value, token);
        }

        private async IAsyncEnumerable<T> Watch<T>([EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var value in WatchAsync(token)) yield return await ProjectAsync<T>(value, token);
        }

        private async ValueTask<T> ProjectAsync<T>(object? value, CancellationToken token)
        {
            var projection = RegressionTestGeneratedProviderProjection.FromCandidate(
                Environment.GetEnvironmentVariable("SUNDER_AGENT_PUBLIC_FIXTURE_CANDIDATE")!, RegressionTestBindings.RuntimeCatalog);
            return (T)(await projection.ProjectAsync(value, typeof(T),
                ProjectionReceiver ?? throw new InvalidOperationException("The generated fixture receiver is required."), token))!;
        }

        public async IAsyncEnumerable<AgentRuntimeCatalogEvent> WatchAsync([EnumeratorCancellation] CancellationToken token = default)
        {
            var events = Channel.CreateBounded<(long Sequence, AgentRuntimeCatalogEvent Event)>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
            var sequence = 0L;
            void Publish(AgentRuntimeCatalogEvent value) => events.Writer.TryWrite((Interlocked.Increment(ref sequence), value));
            void Session(Guid value) => Publish(new AgentSessionCatalogEvent(sequence + 1, value));
            void Turn(Guid sessionId, AgentTurnRecord value) => Publish(new AgentTurnCatalogEvent(sequence + 1, sessionId, value));
            void Profile(string value) => Publish(new AgentProfileCatalogEvent(sequence + 1, value));
            void Reset() => Publish(new AgentRuntimeCatalogResetEvent(sequence + 1));
            SessionChanged += Session; TurnChanged += Turn; ProfileChanged += Profile; ResetRequired += Reset;
            try
            {
                Reset();
                var expected = 1L;
                await foreach (var item in events.Reader.ReadAllAsync(token))
                {
                    var value = item.Sequence == expected ? item.Event : new AgentRuntimeCatalogResetEvent(item.Sequence);
                    expected = item.Sequence + 1;
                    yield return value;
                }
            }
            finally { SessionChanged -= Session; TurnChanged -= Turn; ProfileChanged -= Profile; ResetRequired -= Reset; }
        }
    }
}
