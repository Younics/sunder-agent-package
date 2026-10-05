using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private sealed class MemoryConsumerRpc(ISunderRpcClient inner) : ISunderRpcClient
    {
        internal int DiscoveryCount;
        internal SunderRpcErrorKind? LookupFailure;
        internal bool ChangeEmbeddingGeneration;
        internal Func<SunderRpcProviderSnapshot, SunderRpcProviderSnapshot>? LookupTransform;
        internal ConcurrentBag<ISunderRpcCallScope> Scopes { get; } = [];
        internal int ActiveWatches;
        public async ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            var scope = await inner.CreateCallScopeAsync(options, cancellationToken);
            Scopes.Add(scope);
            return scope;
        }
        public async ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
        {
            if (LookupFailure is { } kind) throw new SunderRpcException(new(kind, "test.lookup-failed", "Test provider lookup failed."));
            var value = await inner.GetProviderAsync(endpoint, cancellationToken);
            if (value is not null && LookupTransform is not null) value = LookupTransform(value);
            return ChangeEmbeddingGeneration && value?.PackageId == "example.embedding" ? value with { SessionGeneration = value.SessionGeneration + 1 } : value;
        }
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default) => inner.TryReportInvariantViolationAsync(endpoint, exception, cancellationToken);
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref DiscoveryCount); return inner.DiscoverAsync(contractId, cancellationToken); }
        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ActiveWatches);
            try
            {
                await foreach (var change in inner.WatchAsync(afterRevision, afterSequence, cancellationToken)) yield return change;
            }
            finally { Interlocked.Decrement(ref ActiveWatches); }
        }
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => inner.InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => inner.SubscribeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
    }

    private sealed class MemoryConsumerEmbedding(string name) : IAgentEmbeddingProvider, IAgentEmbeddingSpaceIdentityProvider
    {
        public AgentEmbeddingProviderDescriptor Descriptor { get; } = new("embedding", name, []);
        internal bool Block;
        internal bool Cancelled;
        internal int GenerationCount;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([new("embedding/model", "Model", 3, true)]);
        public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentEmbeddingProviderReadiness("embedding", AgentProviderReadinessStatus.Ready, "Ready"));
        public ValueTask<string> GetEmbeddingSpaceIdentityAsync(string modelId, CancellationToken cancellationToken = default) => ValueTask.FromResult("stable-space");
        public async ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(string modelId, string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref GenerationCount);
            if (Block)
            {
                Started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return text.Length == 0 ? null : new(modelId, [1, 0, 0]);
        }
        public async ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(string modelId, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            var result = new List<AgentEmbeddingGenerationResult?>();
            foreach (var text in texts) result.Add(await GenerateEmbeddingAsync(modelId, text, cancellationToken));
            return result;
        }
    }

    private class MemoryConsumerRuntime : IAgentRuntimeCatalog
    {
        private Action<Guid>? _sessionChanged;
        public AgentProfileRecord Profile { get; }
        public AgentSessionRecord Session { get; }
        internal int SessionSubscriberCount;
        internal TaskCompletionSource Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal MemoryConsumerRuntime(AgentProfileRecord? profile = null)
        {
            Profile = profile ?? new("profile.generated", "Generated", null, null, null, null, "embedding", "embedding/model", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            Session = new(Guid.NewGuid(), "Session", AgentSessionState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ProfileId: Profile.ProfileId);
        }
        public event Action<Guid>? SessionChanged
        {
            add { _sessionChanged += value; Interlocked.Increment(ref SessionSubscriberCount); Subscribed.TrySetResult(); }
            remove { _sessionChanged -= value; Interlocked.Decrement(ref SessionSubscriberCount); }
        }
        public event Action<Guid, AgentTurnRecord>? TurnChanged;
        public event Action<string>? ProfileChanged;
        public event Action? ResetRequired;
        internal void NotifySession() => _sessionChanged?.Invoke(Session.SessionId);
        internal void NotifyReset() => ResetRequired?.Invoke();
        internal void NotifyTurn(AgentTurnRecord turn) => TurnChanged?.Invoke(turn.SessionId, turn);
        internal void NotifyProfile() => ProfileChanged?.Invoke(Profile.ProfileId);
        public IReadOnlyList<AgentSessionRecord> ListSessions() => [Session];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId) => profileId == Profile.ProfileId ? [Session] : [];
        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId) => [];
        public AgentSessionRecord? GetSession(Guid sessionId) => sessionId == Session.SessionId ? Session : null;
        public virtual ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<AgentWorkspaceRecord>>([]);
        public ValueTask<AgentWorkspaceRecord?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default) => ValueTask.FromResult<AgentWorkspaceRecord?>(null);
        public AgentProfileRecord? GetSessionProfile(Guid sessionId) => sessionId == Session.SessionId ? Profile : null;
        public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => null;
        public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId) => null;
        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;
        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => [];
        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit) => [];
        public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit) => [];
        public IReadOnlyList<AgentProfileRecord> ListProfiles() => [Profile];
        public AgentProfileRecord? GetProfile(string profileId) => profileId == Profile.ProfileId ? Profile : null;
        public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind) => sessionId == Session.SessionId ? GetModelBinding(Profile.ProfileId, capabilityKind) : null;
        public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind) => profileId == Profile.ProfileId && capabilityKind == AgentModelCapabilityKinds.Embedding
            ? new(profileId, capabilityKind, "embedding", "embedding/model", null, Profile.UpdatedAtUtc) : null;
    }
}
