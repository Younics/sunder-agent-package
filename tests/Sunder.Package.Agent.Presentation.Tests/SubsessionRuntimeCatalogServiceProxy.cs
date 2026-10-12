using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Tests;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Presentation.Tests;

// FixtureBridge dispatches the public flattened methods. The generated interface
// implementations are added explicitly below so the registered provider remains typed.
internal sealed class SubsessionRuntimeCatalogServiceProxy : AgentServices.IRuntimeCatalogService
{
    private readonly IAgentRuntimeCatalog _catalog;
    private readonly RegressionTestRuntimeCatalogProvider _fallback;

    internal SubsessionRuntimeCatalogServiceProxy(IAgentRuntimeCatalog catalog)
    {
        _catalog = catalog;
        _fallback = new RegressionTestRuntimeCatalogProvider(catalog);
    }

    internal static AgentServices.IRuntimeCatalogService Create(IAgentRuntimeCatalog catalog) => new SubsessionRuntimeCatalogServiceProxy(catalog);

    public ValueTask<IReadOnlyList<AgentSessionRecord>> ListSessionsAsync(CancellationToken token = default) => _fallback.ListSessionsAsync(token);
    public ValueTask<IReadOnlyList<AgentSessionRecord>> ListSessionsForProfileAsync(string profileId, CancellationToken token = default) => _fallback.ListSessionsForProfileAsync(profileId, token);
    public ValueTask<IReadOnlyList<AgentSessionRecord>> ListSessionsForWorkspaceAsync(string workspaceId, CancellationToken token = default) => _fallback.ListSessionsForWorkspaceAsync(workspaceId, token);
    public ValueTask<AgentSessionRecord?> GetSessionAsync(Guid sessionId, CancellationToken token = default) => _fallback.GetSessionAsync(sessionId, token);
    public ValueTask<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(CancellationToken token = default) => _fallback.ListWorkspacesAsync(token);
    public ValueTask<AgentWorkspaceRecord?> GetWorkspaceAsync(string workspaceId, CancellationToken token = default) => _fallback.GetWorkspaceAsync(workspaceId, token);
    public ValueTask<AgentProfileRecord?> GetSessionProfileAsync(Guid sessionId, CancellationToken token = default) => _fallback.GetSessionProfileAsync(sessionId, token);
    public ValueTask<AgentWorkingSummaryRecord?> GetWorkingSummaryAsync(Guid sessionId, CancellationToken token = default) => _fallback.GetWorkingSummaryAsync(sessionId, token);
    public ValueTask<AgentSessionContextCheckpointRecord?> GetSessionContextCheckpointAsync(Guid sessionId, CancellationToken token = default) => _fallback.GetSessionContextCheckpointAsync(sessionId, token);
    public ValueTask<AgentRunCheckpointRecord?> GetLatestCheckpointAsync(Guid sessionId, CancellationToken token = default) => _fallback.GetLatestCheckpointAsync(sessionId, token);
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListRecentTurnsAsync(Guid sessionId, int limit, CancellationToken token = default) => _fallback.ListRecentTurnsAsync(sessionId, limit, token);
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListTurnsBeforeAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken token = default) => _fallback.ListTurnsBeforeAsync(sessionId, createdAtUtc, turnId, limit, token);
    public ValueTask<IReadOnlyList<AgentTurnRecord>> ListTurnsAfterAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken token = default) => _fallback.ListTurnsAfterAsync(sessionId, createdAtUtc, turnId, limit, token);
    public async ValueTask<IReadOnlyList<AgentTurnRecord>> ListRecentTranscriptHeadersAsync(Guid sessionId, int limit, CancellationToken token = default)
        => await Task.Run(() => _catalog.ListRecentTurns(sessionId, limit), token);
    public async ValueTask<IReadOnlyList<AgentTurnRecord>> ListTranscriptHeadersBeforeAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken token = default)
        => await Task.Run(() => _catalog.ListTurnsBefore(sessionId, createdAtUtc, turnId, limit), token);
    public async ValueTask<IReadOnlyList<AgentTurnRecord>> ListTranscriptHeadersAfterAsync(Guid sessionId, DateTimeOffset createdAtUtc, Guid turnId, int limit, CancellationToken token = default)
        => await Task.Run(() => _catalog.ListTurnsAfter(sessionId, createdAtUtc, turnId, limit), token);
    public ValueTask<AgentTranscriptToolDetailRecord?> GetTranscriptToolDetailAsync(AgentTranscriptToolDetailRequest request, CancellationToken token = default) => _fallback.GetTranscriptToolDetailAsync(request, token);
    public ValueTask<IReadOnlyList<AgentProfileRecord>> ListProfilesAsync(CancellationToken token = default) => _fallback.ListProfilesAsync(token);
    public ValueTask<AgentProfileRecord?> GetProfileAsync(string profileId, CancellationToken token = default) => _fallback.GetProfileAsync(profileId, token);
    public ValueTask<AgentProfileModelBindingRecord?> GetSessionModelBindingAsync(Guid sessionId, string capabilityKind, CancellationToken token = default) => _fallback.GetSessionModelBindingAsync(sessionId, capabilityKind, token);
    public ValueTask<AgentProfileModelBindingRecord?> GetModelBindingAsync(string profileId, string capabilityKind, CancellationToken token = default) => _fallback.GetModelBindingAsync(profileId, capabilityKind, token);
    public IAsyncEnumerable<AgentRuntimeCatalogEvent> WatchAsync(CancellationToken token = default) => _fallback.WatchAsync(token);
}
