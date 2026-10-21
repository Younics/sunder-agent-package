extern alias SubagentsApp;

using SubagentsApp::Sunder.Package.Agent.Contracts.Models;
using SubagentsApp::Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Package.Agent.TestSupport.Avalonia;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using AgentRunCheckpointRecord = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentRunCheckpointRecord;
using AgentTranscriptToolDetailRecord = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTranscriptToolDetailRecord;
using AgentTranscriptToolDetailRequest = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTranscriptToolDetailRequest;
using AgentTurnRecord = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnRecord;

namespace Sunder.Package.Agent.Presentation.Tests;

internal sealed class CatalogBackedSubsessionRuntimeAdapter :
    ISubsessionSessionReader,
    ISubsessionCheckpointReader,
    ISubsessionTranscriptPageReader,
    ISubsessionChangeNotifications,
    IDisposable
{
    private readonly ISubsessionSessionReader _sessions;
    private readonly ISubsessionCheckpointReader _checkpoints;
    private readonly ISubsessionTranscriptPageReader _transcript;
    private readonly ISubsessionChangeNotifications _changes;
    private readonly ISubagentPresentationInitialization? _initialization;
    private readonly IDisposable? _ownedGateway;

    internal CatalogBackedSubsessionRuntimeAdapter(
        IPackageContext context,
        RegressionTestExtensionCatalog catalog)
        : this(WorkerBackedSubagentManagementGateway.ForRuntime(context, catalog), ownsGateway: true) { }

    internal CatalogBackedSubsessionRuntimeAdapter(WorkerBackedSubagentManagementGateway gateway)
        : this(gateway, ownsGateway: false) { }

    internal CatalogBackedSubsessionRuntimeAdapter(ISunderRpcClient rpc)
        : this(new FailingSubsessionGateway(rpc), ownsGateway: true) { }

    private CatalogBackedSubsessionRuntimeAdapter(
        ISubsessionSessionReader gateway,
        bool ownsGateway)
    {
        _sessions = gateway;
        _checkpoints = (ISubsessionCheckpointReader)gateway;
        _transcript = (ISubsessionTranscriptPageReader)gateway;
        _changes = (ISubsessionChangeNotifications)gateway;
        _initialization = gateway as ISubagentPresentationInitialization;
        _ownedGateway = ownsGateway ? (IDisposable)gateway : null;
    }

    public event Action<Guid>? SessionChanged
    {
        add => _changes.SessionChanged += value;
        remove => _changes.SessionChanged -= value;
    }

    public event Action<Guid, AgentTurnRecord>? TurnChanged
    {
        add => _changes.TurnChanged += value;
        remove => _changes.TurnChanged -= value;
    }

    public event Action? ResnapshotRequired
    {
        add => _changes.ResnapshotRequired += value;
        remove => _changes.ResnapshotRequired -= value;
    }

    public async Task<SubsessionSessionCatalog> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await _sessions.ListSessionsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await _checkpoints.ListLatestCheckpointsAsync(cancellationToken);
    }

    public async Task<SubsessionTranscriptPage> ListRecentTurnsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await _transcript.ListRecentTurnsAsync(sessionId, limit, cancellationToken);
    }

    public async Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(
        Guid sessionId,
        DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await _transcript.ListTurnsBeforeAsync(sessionId, beforeCreatedAtUtc, beforeTurnId, limit, cancellationToken);
    }

    public async Task<SubsessionTranscriptPage> ListTurnsAfterAsync(
        Guid sessionId,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await _transcript.ListTurnsAfterAsync(sessionId, afterCreatedAtUtc, afterTurnId, limit, cancellationToken);
    }

    public async Task<SubsessionAroundTurnPage> LoadAroundTurnAsync(
        Guid sessionId,
        Guid turnId,
        DateTimeOffset turnCreatedAtUtc,
        Guid itemId,
        int beforeLimit,
        int afterLimit,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await _transcript.LoadAroundTurnAsync(sessionId, turnId, turnCreatedAtUtc, itemId, beforeLimit, afterLimit, cancellationToken);
    }

    public async Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await _transcript.LoadToolDetailAsync(request, cancellationToken);
    }

    public SubsessionsViewModel CreateViewModel()
        => new(this, this, this, this);

    public void Dispose() => _ownedGateway?.Dispose();

    private Task EnsureInitializedAsync(CancellationToken cancellationToken)
        => _initialization?.InitializeAsync(cancellationToken) ?? Task.CompletedTask;

    private sealed class FailingSubsessionGateway(ISunderRpcClient rpc) :
        ISubsessionSessionReader,
        ISubsessionCheckpointReader,
        ISubsessionTranscriptPageReader,
        ISubsessionChangeNotifications,
        IDisposable
    {
        public event Action<Guid>? SessionChanged { add { } remove { } }
        public event Action<Guid, AgentTurnRecord>? TurnChanged { add { } remove { } }
        public event Action? ResnapshotRequired { add { } remove { } }

        public Task<SubsessionSessionCatalog> ListSessionsAsync(CancellationToken cancellationToken = default)
            => FailAsync<SubsessionSessionCatalog>(cancellationToken);

        public Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(
            CancellationToken cancellationToken = default)
            => FailAsync<IReadOnlyList<AgentRunCheckpointRecord>>(cancellationToken);

        public Task<SubsessionTranscriptPage> ListRecentTurnsAsync(Guid sessionId, int limit, CancellationToken cancellationToken = default)
            => FailAsync<SubsessionTranscriptPage>(cancellationToken);

        public Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit, CancellationToken cancellationToken = default)
            => FailAsync<SubsessionTranscriptPage>(cancellationToken);

        public Task<SubsessionTranscriptPage> ListTurnsAfterAsync(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit, CancellationToken cancellationToken = default)
            => FailAsync<SubsessionTranscriptPage>(cancellationToken);

        public Task<SubsessionAroundTurnPage> LoadAroundTurnAsync(Guid sessionId, Guid turnId, DateTimeOffset turnCreatedAtUtc, Guid itemId, int beforeLimit, int afterLimit, CancellationToken cancellationToken = default)
            => FailAsync<SubsessionAroundTurnPage>(cancellationToken);

        public Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(AgentTranscriptToolDetailRequest request, CancellationToken cancellationToken = default)
            => FailAsync<AgentTranscriptToolDetailRecord?>(cancellationToken);

        public void Dispose() { }

        private async Task<T> FailAsync<T>(CancellationToken cancellationToken)
        {
            _ = await rpc.DiscoverAsync("younics.ai.agent.subagents.owner", cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The Subagents management service is unavailable.");
        }
    }
}
