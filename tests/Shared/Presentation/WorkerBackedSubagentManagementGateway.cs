extern alias SubagentsApp;
extern alias SubagentsWorker;

using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using SubagentManagementServices = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Subagents_Services;
using SubagentsApp::Sunder.Package.Agent.Subagents.Models;
using SubagentsApp::Sunder.Package.Agent.Subagents.Runtime;
using SubagentsApp::Sunder.Package.Agent.Shared.Presentation;
using WorkerService = SubagentsWorker::Sunder.Package.Agent.Subagents.Services.SubagentService;
using AgentProfileSelectableCapabilityDescriptor = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentProfileSelectableCapabilityDescriptor;
using AgentRunCheckpointRecord = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentRunCheckpointRecord;
using AgentRunActivityUpdate = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentRunActivityUpdate;
using AgentToolDescriptor = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentToolDescriptor;
using AgentTranscriptToolDetailRecord = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTranscriptToolDetailRecord;
using AgentTranscriptToolDetailRequest = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTranscriptToolDetailRequest;
using AgentTurnRecord = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnRecord;
using AgentCompactTranscriptRequest = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentCompactTranscriptRequest;
using AgentCompactTranscriptPage = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentCompactTranscriptPage;
using AgentTranscriptActivityMembersRequest = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTranscriptActivityMembersRequest;
using AgentTranscriptActivityMembersPage = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTranscriptActivityMembersPage;
using AgentTranscriptActivityGroup = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTranscriptActivityGroup;

namespace Sunder.Package.Agent.TestSupport.Avalonia;

// Real Worker operations -> generated handler -> scoped content -> generated App
// client. Domain models never cross this boundary by CLR cast or shared reference.
internal sealed class WorkerBackedSubagentManagementGateway : ISubagentManagementGateway,
    ISubsessionSessionReader, ISubsessionCheckpointReader, ISubsessionTranscriptPageReader,
    ISubsessionCompactTranscriptReader,
    ISubsessionChangeNotifications, ISubagentPresentationInitialization, IDisposable, IAsyncDisposable
{
    private const string PackageId = "younics.ai.agent.subagents";
    private readonly RegressionTestExtensionCatalog _catalog;
    private readonly RegressionSubagentManagement _management;
    private readonly SubagentAppRuntimeGateway _app;
    private readonly Task _initialization;
    private Task? _disposal;
    private readonly object _gate = new();

    internal static WorkerBackedSubagentManagementGateway ForRuntime(IPackageContext context, RegressionTestExtensionCatalog catalog)
        => new(new WorkerService(new SubagentsWorker::Sunder.Package.Agent.Subagents.Services.SubagentStore(context)), catalog);

    internal WorkerBackedSubagentManagementGateway(WorkerService service, RegressionTestExtensionCatalog catalog)
    {
        _catalog = catalog;
        _management = catalog.CreateSubagentRuntimeManagementAsync(service).AsTask().GetAwaiter().GetResult();
        var receiver = catalog.Receiver(PackageId);
        var registration = receiver.Register(_management.Service, "younics.ai.agent.subagents.owner");
        catalog.AddGeneratedProvider(PackageId, _management.Service, registration);
        var providers = receiver
            .Providers<SubagentManagementServices.ISubagentsManagementService>()
            .ListAsync()
            .AsTask()
            .GetAwaiter()
            .GetResult();
        _app = new SubagentAppRuntimeGateway(providers.Providers.Single().Service);
        // Avoid capturing an Avalonia dispatcher for the transport observation loop.
        _initialization = Task.Run(() => _app.InitializeAsync());
    }

    public event Action? SubagentsChanged
    {
        add => _app.SubagentsChanged += value;
        remove => _app.SubagentsChanged -= value;
    }

    public event Action? CatalogChanged
    {
        add => _app.CatalogChanged += value;
        remove => _app.CatalogChanged -= value;
    }

    public event Action<Guid>? SessionChanged { add => _app.SessionChanged += value; remove => _app.SessionChanged -= value; }
    public event Action<Guid, AgentTurnRecord, AgentTranscriptActivityGroup?>? TurnChanged { add => _app.TurnChanged += value; remove => _app.TurnChanged -= value; }
    public event Action<Guid, AgentRunActivityUpdate>? RunActivityChanged { add => _app.RunActivityChanged += value; remove => _app.RunActivityChanged -= value; }
    public event Action? ResnapshotRequired { add => _app.ResnapshotRequired += value; remove => _app.ResnapshotRequired -= value; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => _initialization.WaitAsync(cancellationToken);
    public Task<SubsessionSessionCatalog> ListSessionsAsync(CancellationToken cancellationToken = default) => _app.ListSessionsAsync(cancellationToken);
    public Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(CancellationToken cancellationToken = default) => _app.ListLatestCheckpointsAsync(cancellationToken);
    public Task<SubsessionTranscriptPage> ListRecentTurnsAsync(Guid sessionId, int limit, CancellationToken cancellationToken = default)
        => _app.ListRecentTurnsAsync(sessionId, limit, cancellationToken);
    public Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit, CancellationToken cancellationToken = default)
        => _app.ListTurnsBeforeAsync(sessionId, beforeCreatedAtUtc, beforeTurnId, limit, cancellationToken);
    public Task<SubsessionTranscriptPage> ListTurnsAfterAsync(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit, CancellationToken cancellationToken = default)
        => _app.ListTurnsAfterAsync(sessionId, afterCreatedAtUtc, afterTurnId, limit, cancellationToken);
    public Task<SubsessionAroundTurnPage> LoadAroundTurnAsync(Guid sessionId, Guid turnId, DateTimeOffset turnCreatedAtUtc, Guid itemId,
        int beforeLimit, int afterLimit, CancellationToken cancellationToken = default)
        => _app.LoadAroundTurnAsync(sessionId, turnId, turnCreatedAtUtc, itemId, beforeLimit, afterLimit, cancellationToken);
    public Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(AgentTranscriptToolDetailRequest request, CancellationToken cancellationToken = default)
        => _app.LoadToolDetailAsync(request, cancellationToken);
    public Task<AgentCompactTranscriptPage> LoadCompactTranscriptAsync(AgentCompactTranscriptRequest request, CancellationToken cancellationToken = default)
        => _app.LoadCompactTranscriptAsync(request, cancellationToken);
    public Task<AgentTranscriptActivityMembersPage> LoadActivityMembersAsync(AgentTranscriptActivityMembersRequest request, CancellationToken cancellationToken = default)
        => _app.LoadActivityMembersAsync(request, cancellationToken);

    public Task<IReadOnlyList<SubagentRecord>> ListSubagentsAsync(CancellationToken cancellationToken = default)
        => _app.ListSubagentsAsync(cancellationToken);

    public Task<SubagentRecord> CreateSubagentAsync(string displayName, CancellationToken cancellationToken = default)
        => _app.CreateSubagentAsync(displayName, cancellationToken);

    public Task<SubagentRecord> SaveSubagentAsync(SubagentSaveRequest request, CancellationToken cancellationToken = default)
        => _app.SaveSubagentAsync(request, cancellationToken);

    public Task DeleteSubagentAsync(string subagentId, CancellationToken cancellationToken = default)
        => _app.DeleteSubagentAsync(subagentId, cancellationToken);

    public Task<IReadOnlyList<ProviderCatalogOption>> ListChatProvidersAsync(CancellationToken cancellationToken = default)
        => _app.ListChatProvidersAsync(cancellationToken);

    public Task<ProviderModelCatalogResult> LoadChatModelsAsync(string providerId, CancellationToken cancellationToken = default)
        => _app.LoadChatModelsAsync(providerId, cancellationToken);

    public Task<IReadOnlyList<AgentToolDescriptor>> ListLocalToolsAsync(CancellationToken cancellationToken = default)
        => _app.ListLocalToolsAsync(cancellationToken);

    public Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListPackageCapabilitiesAsync(CancellationToken cancellationToken = default)
        => _app.ListPackageCapabilitiesAsync(cancellationToken);

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposal ??= StopAsync());
    }

    private async Task StopAsync()
    {
        try
        {
            await _initialization.ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try { await _catalog.RetireProviderAsync(_management.Service).ConfigureAwait(false); }
            finally
            {
                await _management.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
