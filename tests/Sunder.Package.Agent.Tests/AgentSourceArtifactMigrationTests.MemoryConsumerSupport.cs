using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

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

    private sealed class MemoryConsumerEmbedding(string name) : AgentServices.IEmbeddingProviderService,
        IAgentEmbeddingProvider, IAgentEmbeddingSpaceIdentityProvider
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

        ValueTask<AgentEmbeddingProviderDescriptor> AgentServices.IEmbeddingProviderService.DescribeAsync(CancellationToken token)
            => ValueTask.FromResult(Descriptor);
        ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> AgentServices.IEmbeddingProviderService.ListModelsAsync(CancellationToken token)
            => GetAvailableModelsAsync(token);
        ValueTask<AgentEmbeddingProviderReadiness> AgentServices.IEmbeddingProviderService.GetReadinessAsync(CancellationToken token)
            => GetReadinessAsync(token);
        ValueTask<string> AgentServices.IEmbeddingProviderService.SpaceIdentityAsync(string request, CancellationToken token)
            => GetEmbeddingSpaceIdentityAsync(request, token);
        ValueTask<AgentEmbeddingGenerationResult?> AgentServices.IEmbeddingProviderService.GenerateAsync(
            string modelId, string text, CancellationToken token)
            => GenerateEmbeddingAsync(modelId, text, token);
        ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> AgentServices.IEmbeddingProviderService.GenerateBatchAsync(
            string modelId, IReadOnlyList<string> texts, CancellationToken token)
            => GenerateEmbeddingsAsync(modelId, texts, token);
    }

    private class MemoryConsumerRuntime : AgentServices.IRuntimeCatalogService, IAgentRuntimeCatalog
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

        ValueTask<AgentRunCheckpointRecord?> AgentServices.IRuntimeCatalogService.GetLatestCheckpointAsync(Guid request, CancellationToken token)
            => ValueTask.FromResult(GetLatestCheckpoint(request));
        ValueTask<Sunder.Package.Agent.Models.AgentRunActivityUpdate?> AgentServices.IRuntimeCatalogService.GetRunActivityAsync(Guid request, CancellationToken token)
            => ValueTask.FromResult<Sunder.Package.Agent.Models.AgentRunActivityUpdate?>(null);
        ValueTask<AgentProfileModelBindingRecord?> AgentServices.IRuntimeCatalogService.GetModelBindingAsync(
            string profileId, string capabilityKind, CancellationToken token)
            => ValueTask.FromResult(GetModelBinding(profileId, capabilityKind));
        ValueTask<AgentProfileRecord?> AgentServices.IRuntimeCatalogService.GetProfileAsync(string request, CancellationToken token)
            => ValueTask.FromResult(GetProfile(request));
        ValueTask<AgentSessionRecord?> AgentServices.IRuntimeCatalogService.GetSessionAsync(Guid request, CancellationToken token)
            => ValueTask.FromResult(GetSession(request));
        ValueTask<AgentSessionContextCheckpointRecord?> AgentServices.IRuntimeCatalogService.GetSessionContextCheckpointAsync(Guid request, CancellationToken token)
            => ValueTask.FromResult(GetLatestSessionContextCheckpoint(request));
        ValueTask<AgentProfileModelBindingRecord?> AgentServices.IRuntimeCatalogService.GetSessionModelBindingAsync(
            Guid sessionId, string capabilityKind, CancellationToken token)
            => ValueTask.FromResult(GetSessionModelBinding(sessionId, capabilityKind));
        ValueTask<AgentProfileRecord?> AgentServices.IRuntimeCatalogService.GetSessionProfileAsync(Guid request, CancellationToken token)
            => ValueTask.FromResult(GetSessionProfile(request));
        ValueTask<AgentWorkingSummaryRecord?> AgentServices.IRuntimeCatalogService.GetWorkingSummaryAsync(Guid request, CancellationToken token)
            => ValueTask.FromResult(GetWorkingSummary(request));
        ValueTask<AgentWorkspaceRecord?> AgentServices.IRuntimeCatalogService.GetWorkspaceAsync(string request, CancellationToken token)
            => GetWorkspaceAsync(request, token);
        ValueTask<IReadOnlyList<AgentProfileRecord>> AgentServices.IRuntimeCatalogService.ListProfilesAsync(CancellationToken token)
            => ValueTask.FromResult(ListProfiles());
        ValueTask<IReadOnlyList<AgentSessionRecord>> AgentServices.IRuntimeCatalogService.ListSessionsAsync(CancellationToken token)
            => ValueTask.FromResult(ListSessions());
        ValueTask<IReadOnlyList<AgentSessionRecord>> AgentServices.IRuntimeCatalogService.ListSessionsForProfileAsync(string request, CancellationToken token)
            => ValueTask.FromResult(ListSessionsForProfile(request));
        ValueTask<IReadOnlyList<AgentSessionRecord>> AgentServices.IRuntimeCatalogService.ListSessionsForWorkspaceAsync(string request, CancellationToken token)
            => ValueTask.FromResult(ListSessionsForWorkspace(request));
        ValueTask<IReadOnlyList<AgentWorkspaceRecord>> AgentServices.IRuntimeCatalogService.ListWorkspacesAsync(CancellationToken token)
            => ListWorkspacesAsync(token);
        ValueTask<AgentTranscriptToolDetailRecord?> AgentServices.IRuntimeCatalogService.GetTranscriptToolDetailAsync(
            AgentTranscriptToolDetailRequest request, CancellationToken token)
            => ValueTask.FromResult<AgentTranscriptToolDetailRecord?>(null);
        public virtual ValueTask<AgentCompactTranscriptPage> ReadCompactTranscriptAsync(AgentCompactTranscriptRequest request, CancellationToken token = default)
            => ValueTask.FromResult(new AgentCompactTranscriptPage([], false, false));
        public virtual ValueTask<AgentTranscriptActivityMembersPage> ReadActivityMembersAsync(AgentTranscriptActivityMembersRequest request, CancellationToken token = default)
            => ValueTask.FromResult(new AgentTranscriptActivityMembersPage([], false, null));
        ValueTask<IReadOnlyList<AgentTurnRecord>> AgentServices.IRuntimeCatalogService.ListRecentTranscriptHeadersAsync(
            Guid sessionId, int limit, CancellationToken token)
            => ValueTask.FromResult<IReadOnlyList<AgentTurnRecord>>([]);
        ValueTask<IReadOnlyList<AgentTurnRecord>> AgentServices.IRuntimeCatalogService.ListRecentTurnsAsync(
            Guid sessionId, int limit, CancellationToken token)
            => ValueTask.FromResult(ListRecentTurns(sessionId, limit));
        ValueTask<IReadOnlyList<AgentTurnRecord>> AgentServices.IRuntimeCatalogService.ListTranscriptHeadersAfterAsync(
            Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken token)
            => ValueTask.FromResult<IReadOnlyList<AgentTurnRecord>>([]);
        ValueTask<IReadOnlyList<AgentTurnRecord>> AgentServices.IRuntimeCatalogService.ListTranscriptHeadersBeforeAsync(
            Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken token)
            => ValueTask.FromResult<IReadOnlyList<AgentTurnRecord>>([]);
        ValueTask<IReadOnlyList<AgentTurnRecord>> AgentServices.IRuntimeCatalogService.ListTurnsAfterAsync(
            Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken token)
            => ValueTask.FromResult<IReadOnlyList<AgentTurnRecord>>([]);
        ValueTask<IReadOnlyList<AgentTurnRecord>> AgentServices.IRuntimeCatalogService.ListTurnsBeforeAsync(
            Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken token)
            => ValueTask.FromResult<IReadOnlyList<AgentTurnRecord>>([]);
        async IAsyncEnumerable<AgentRuntimeCatalogEvent> AgentServices.IRuntimeCatalogService.WatchAsync(
            [EnumeratorCancellation] CancellationToken token)
        {
            var events = System.Threading.Channels.Channel.CreateUnbounded<AgentRuntimeCatalogEvent>();
            var sequence = 0L;
            void Session(Guid value) => events.Writer.TryWrite(new AgentSessionCatalogEvent(Interlocked.Increment(ref sequence), value));
            void Reset() => events.Writer.TryWrite(new AgentRuntimeCatalogResetEvent(Interlocked.Increment(ref sequence)));
            SessionChanged += Session;
            ResetRequired += Reset;
            try
            {
                Reset();
                await foreach (var value in events.Reader.ReadAllAsync(token)) yield return value;
            }
            finally
            {
                SessionChanged -= Session;
                ResetRequired -= Reset;
            }
        }
    }
}
