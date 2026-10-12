extern alias AgentApp;

using AgentApp::Sunder.Package.Agent.Contracts.Models;
using AgentApp::Sunder.Package.Agent.HistorySearch;
using AgentApp::Sunder.Package.Agent.Runtime;
using AgentApp::Sunder.Package.Agent.Services;
using AgentServices = AgentApp::Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Tests;

// The presentation race fixtures control domain requests and completion, not a
// replacement transport. Their common stub implements the real generated interface.
internal abstract class OwnerServiceStub : AgentServices.IAgentRuntimeService, IAgentOwnerResources, AgentServices.IAgentProfileService
{
    public virtual AgentTransportProfile TransportProfile => AgentTransportProfile.StandardFallback;
    public virtual ValueTask<AgentTransportProfile> GetTransportProfileAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(TransportProfile);
    public virtual ValueTask<AgentTransportProfile> NegotiateTransportProfileAsync(AgentTransportProfileRequest request, CancellationToken cancellationToken)
        => GetTransportProfileAsync(cancellationToken);

    public abstract ValueTask<TResponse> RespondAsync<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : class where TResponse : class;
    public abstract IAsyncEnumerable<TEvent> ObserveAsync<TRequest, TEvent>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : class where TEvent : class;

    public ValueTask<AgentChatSnapshotProjection> GetChatSnapshotAsync(AgentChatSnapshotRequest request, CancellationToken token)
        => RespondAsync<AgentChatSnapshotRequest, AgentChatSnapshotProjection>(request, token);
    public ValueTask<AgentDashboardProjection> GetDashboardAsync(AgentDashboardRequest request, CancellationToken token)
        => RespondAsync<AgentDashboardRequest, AgentDashboardProjection>(request, token);
    public ValueTask<AgentTranscriptPage> GetTranscriptPageAsync(AgentTranscriptPageRequest request, CancellationToken token)
        => RespondAsync<AgentTranscriptPageRequest, AgentTranscriptPage>(request, token);
    public ValueTask<AgentCatalogProjection> GetCatalogAsync(AgentCatalogRequest request, CancellationToken token)
        => RespondAsync<AgentCatalogRequest, AgentCatalogProjection>(request, token);
    public ValueTask<AgentProfileCommandResult> ExecuteProfileCommandAsync(AgentProfileCommand request, CancellationToken token)
        => RespondAsync<AgentProfileCommand, AgentProfileCommandResult>(request, token);
    public ValueTask<AgentWorkspaceCommandResult> ExecuteWorkspaceCommandAsync(AgentWorkspaceCommand request, CancellationToken token)
        => RespondAsync<AgentWorkspaceCommand, AgentWorkspaceCommandResult>(request, token);
    public ValueTask<AgentSessionCommandResult> ExecuteSessionCommandAsync(AgentSessionCommand request, CancellationToken token)
        => RespondAsync<AgentSessionCommand, AgentSessionCommandResult>(request, token);
    public ValueTask<AgentRunCommandResult> ExecuteRunCommandAsync(AgentRunCommand request, CancellationToken token)
        => RespondAsync<AgentRunCommand, AgentRunCommandResult>(request, token);
    public ValueTask<AgentRunCommandStatusResult> GetRunStatusAsync(AgentRunCommandStatusRequest request, CancellationToken token)
        => RespondAsync<AgentRunCommandStatusRequest, AgentRunCommandStatusResult>(request, token);
    public ValueTask<AgentPermissionProjection> ExecutePermissionCommandAsync(AgentPermissionCommand request, CancellationToken token)
        => RespondAsync<AgentPermissionCommand, AgentPermissionProjection>(request, token);
    public ValueTask<AgentAttachmentContentUploadResult> UploadAttachmentAsync(AgentAttachmentContentUploadRequest request, CancellationToken token)
        => RespondAsync<AgentAttachmentContentUploadRequest, AgentAttachmentContentUploadResult>(request, token);
    public ValueTask<AgentAttachmentContentDownloadResult> DownloadAttachmentAsync(AgentAttachmentContentDownloadRequest request, CancellationToken token)
        => RespondAsync<AgentAttachmentContentDownloadRequest, AgentAttachmentContentDownloadResult>(request, token);
    public ValueTask<AgentAttachmentContentDiscardResult> DiscardAttachmentAsync(AgentAttachmentContentDiscardRequest request, CancellationToken token)
        => RespondAsync<AgentAttachmentContentDiscardRequest, AgentAttachmentContentDiscardResult>(request, token);
    public ValueTask<HistorySearchResponse> SearchHistoryAsync(HistorySearchRequest request, CancellationToken token)
        => RespondAsync<HistorySearchRequest, HistorySearchResponse>(request, token);
    public ValueTask<HistorySearchState> GetHistoryStateAsync(HistorySearchStateRequest request, CancellationToken token)
        => RespondAsync<HistorySearchStateRequest, HistorySearchState>(request, token);
    public ValueTask<HistorySearchCommandResult> ExecuteHistoryCommandAsync(HistorySearchCommand request, CancellationToken token)
        => RespondAsync<HistorySearchCommand, HistorySearchCommandResult>(request, token);
    public ValueTask<AgentTranscriptAroundTurnPage> GetTranscriptAroundAsync(AgentTranscriptAroundTurnRequest request, CancellationToken token)
        => RespondAsync<AgentTranscriptAroundTurnRequest, AgentTranscriptAroundTurnPage>(request, token);
    public ValueTask<AgentTranscriptToolDetailResult> GetTranscriptToolDetailAsync(AgentTranscriptToolDetailRequest request, CancellationToken token)
        => RespondAsync<AgentTranscriptToolDetailRequest, AgentTranscriptToolDetailResult>(request, token);
    public async ValueTask<AgentToolPresentation?> GetToolPresentationAsync(AgentToolPresentationRequest request, CancellationToken token)
        => await RespondAsync<AgentToolPresentationRequest, AgentToolPresentation>(request, token);
    public IAsyncEnumerable<AgentRuntimeChange> WatchChangesAsync(AgentChangeSubscription request, CancellationToken token)
        => ObserveAsync<AgentChangeSubscription, AgentRuntimeChange>(request, token);
    public IAsyncEnumerable<HistorySearchStatus> WatchHistoryStatusAsync(HistorySearchStatusSubscription request, CancellationToken token)
        => ObserveAsync<HistorySearchStatusSubscription, HistorySearchStatus>(request, token);

    public virtual ValueTask<AgentAttachmentUploadHandle> UploadAttachmentAsync(AgentAttachmentUploadRequest upload, CancellationToken token)
        => ValueTask.FromException<AgentAttachmentUploadHandle>(new NotSupportedException());
    public virtual ValueTask<byte[]> DownloadAttachmentAsync(AgentAttachmentMetadata metadata, CancellationToken token)
        => ValueTask.FromException<byte[]>(new NotSupportedException());
    public virtual ValueTask DiscardAttachmentAsync(AgentAttachmentUploadHandle handle, CancellationToken token) => ValueTask.CompletedTask;

    public virtual async Task<IReadOnlyList<AgentProfileRecord>> ListProfilesAsync(CancellationToken token = default)
        => (await GetDashboardAsync(new(), token)).Profiles;
    public async Task<AgentProfileRecord?> GetProfileAsync(string id, CancellationToken token = default)
        => (await ListProfilesAsync(token)).FirstOrDefault(value => value.ProfileId == id);
    public async Task<AgentProfileRecord> CreateProfileAsync(string name, CancellationToken token = default)
        => (await ExecuteProfileCommandAsync(new(AgentProfileCommandKind.Create, DisplayName: name), token)).Profile!;
    public async Task SaveProfileAsync(string id, string name, string? description, string? instructions,
        string? chatProvider, string? chatModel, string? embeddingProvider, string? embeddingModel,
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? assignments = null, string? loop = null,
        string? source = null, string? settings = null, string? chatSettings = null, CancellationToken token = default)
        => _ = await ExecuteProfileCommandAsync(new(AgentProfileCommandKind.Save, id, name, description, instructions,
            chatProvider, chatModel, embeddingProvider, embeddingModel, assignments, loop, source, settings, chatSettings), token);
    public async Task DeleteProfileAsync(string id, CancellationToken token = default)
        => _ = await ExecuteProfileCommandAsync(new(AgentProfileCommandKind.Delete, ProfileId: id), token);
    public async Task<IReadOnlyList<AgentBehaviorLoopDescriptor>> ListBehaviorLoopDescriptorsAsync(CancellationToken token = default)
        => (await GetCatalogAsync(new(), token)).BehaviorLoops;
    public async Task<IReadOnlyList<AgentProviderDescriptor>> ListChatProviderDescriptorsAsync(CancellationToken token = default)
        => (await GetCatalogAsync(new(), token)).ChatProviders;
    public async Task<IReadOnlyList<AgentEmbeddingProviderDescriptor>> ListEmbeddingProviderDescriptorsAsync(CancellationToken token = default)
        => (await GetCatalogAsync(new(), token)).EmbeddingProviders;
    public async Task<bool> HasProfileCapabilityConsumersAsync(string kind, CancellationToken token = default)
        => (await GetCatalogAsync(new(), token)).HasEmbeddingConsumers;
    public async Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListSelectableProfileCapabilitiesAsync(AgentProfileRecord? profile = null, CancellationToken token = default)
        => (await GetCatalogAsync(new(Profile: profile), token)).SelectableCapabilities;
    public async Task<IReadOnlyList<AgentToolCatalogEntry>> ListInstalledLocalToolsAsync(CancellationToken token = default)
        => (await GetCatalogAsync(new(), token)).LocalTools;
    public async Task<IReadOnlyList<AgentModelDescriptor>> ListChatModelsAsync(string? provider, CancellationToken token = default)
        => (await GetCatalogAsync(new(ChatProviderId: provider), token)).ChatModels;
    public async Task<IReadOnlyList<AgentEmbeddingModelDescriptor>> ListEmbeddingModelsAsync(string? provider, CancellationToken token = default)
        => (await GetCatalogAsync(new(EmbeddingProviderId: provider), token)).EmbeddingModels;
    public async Task<AgentProviderReadiness?> GetChatProviderReadinessAsync(string? provider, CancellationToken token = default)
        => (await GetCatalogAsync(new(ChatProviderId: provider), token)).ChatReadiness;
    public async Task<AgentEmbeddingProviderReadiness?> GetEmbeddingProviderReadinessAsync(string? provider, CancellationToken token = default)
        => (await GetCatalogAsync(new(EmbeddingProviderId: provider), token)).EmbeddingReadiness;
}
