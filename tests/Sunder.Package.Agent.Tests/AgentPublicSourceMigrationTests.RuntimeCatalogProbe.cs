using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private sealed class RuntimeCatalogProbe(IAgentRuntimeCatalog inner) : IAgentRuntimeCatalog
    {
        public event Action<Guid>? SessionChanged;
        public event Action<Guid, AgentTurnRecord>? TurnChanged;
        public event Action<string>? ProfileChanged;
        public event Action? ResetRequired;
        public int SubscriberCount => (SessionChanged?.GetInvocationList().Length ?? 0) + (TurnChanged?.GetInvocationList().Length ?? 0)
            + (ProfileChanged?.GetInvocationList().Length ?? 0) + (ResetRequired?.GetInvocationList().Length ?? 0);
        public bool Fail { get; set; }
        public bool BlockWorkspaces { get; set; }
        public TaskCompletionSource Started { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void RaiseSession(Guid id) => SessionChanged?.Invoke(id);
        public void RaiseTurn(AgentTurnRecord turn) => TurnChanged?.Invoke(turn.SessionId, turn);
        public void RaiseProfile(string id) => ProfileChanged?.Invoke(id);
        public void RaiseReset() => ResetRequired?.Invoke();
        public IReadOnlyList<AgentSessionRecord> ListSessions()
            => Fail ? throw new IOException("private catalog storage detail") : inner.ListSessions();
        public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId) => inner.ListSessionsForProfile(profileId);
        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId) => inner.ListSessionsForWorkspace(workspaceId);
        public AgentSessionRecord? GetSession(Guid sessionId) => inner.GetSession(sessionId);
        public async ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(CancellationToken cancellationToken = default)
        {
            if (BlockWorkspaces) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return await inner.ListWorkspacesAsync(cancellationToken);
        }
        public async ValueTask<AgentWorkspaceRecord?> GetWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
        {
            if (BlockWorkspaces) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return await inner.GetWorkspaceAsync(workspaceId, cancellationToken);
        }
        public AgentProfileRecord? GetSessionProfile(Guid sessionId) => inner.GetSessionProfile(sessionId);
        public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => inner.GetWorkingSummary(sessionId);
        public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId) => inner.GetLatestSessionContextCheckpoint(sessionId);
        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => inner.GetLatestCheckpoint(sessionId);
        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => inner.ListRecentTurns(sessionId, limit);
        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit)
            => inner.ListTurnsBefore(sessionId, beforeCreatedAtUtc, beforeTurnId, limit);
        public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit)
            => inner.ListTurnsAfter(sessionId, afterCreatedAtUtc, afterTurnId, limit);
        public IReadOnlyList<AgentProfileRecord> ListProfiles() => inner.ListProfiles();
        public AgentProfileRecord? GetProfile(string profileId) => inner.GetProfile(profileId);
        public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind) => inner.GetSessionModelBinding(sessionId, capabilityKind);
        public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind) => inner.GetModelBinding(profileId, capabilityKind);
    }
}
