extern alias AgentApp;
using AgentApp::Sunder.Package.Agent.Contracts.Models;
using AgentApp::Sunder.Package.Agent.Runtime;
using AgentApp::Sunder.Package.Agent.Services;
using AgentServices = AgentApp::Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Tests;

// Presentation fakes retain their mutation/dispatcher controls while implementing
// the actual generated API. These defaults adapt local fake state, never RPC.
internal interface IProfileServiceFixture : IAgentProfileGateway, AgentServices.IAgentProfileService
{
    Task<IReadOnlyList<AgentProfileRecord>> AgentServices.IAgentProfileService.ListProfilesAsync(CancellationToken token)
        => Task.FromResult(ListProfiles());
    Task<AgentProfileRecord?> AgentServices.IAgentProfileService.GetProfileAsync(string id, CancellationToken token)
        => Task.FromResult(GetProfile(id));
    Task<IReadOnlyList<AgentBehaviorLoopDescriptor>> AgentServices.IAgentProfileService.ListBehaviorLoopDescriptorsAsync(CancellationToken token)
        => ((IAgentProfileGateway)this).ListBehaviorLoopDescriptorsAsync(token);
    Task<IReadOnlyList<AgentProviderDescriptor>> AgentServices.IAgentProfileService.ListChatProviderDescriptorsAsync(CancellationToken token)
        => ((IAgentProfileGateway)this).ListChatProviderDescriptorsAsync(token);
    Task<IReadOnlyList<AgentEmbeddingProviderDescriptor>> AgentServices.IAgentProfileService.ListEmbeddingProviderDescriptorsAsync(CancellationToken token)
        => ((IAgentProfileGateway)this).ListEmbeddingProviderDescriptorsAsync(token);
    Task<bool> AgentServices.IAgentProfileService.HasProfileCapabilityConsumersAsync(string kind, CancellationToken token)
        => ((IAgentProfileGateway)this).HasProfileCapabilityConsumersAsync(kind, token);
    Task AgentServices.IAgentProfileService.SaveProfileAsync(string id, string name, string? description, string? instructions,
        string? chatProvider, string? chatModel, string? embeddingProvider, string? embeddingModel,
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? assignments, string? loop, string? source,
        string? settings, string? chatSettings, CancellationToken token)
    {
        if (this is IAgentProfileCommandGateway commands)
            return commands.SaveProfileAsync(id, name, description, instructions, chatProvider, chatModel,
                embeddingProvider, embeddingModel, assignments, loop, source, settings, chatSettings, token);
        SaveProfile(id, name, description, instructions, chatProvider, chatModel, embeddingProvider, embeddingModel,
            assignments, loop, source, settings, chatSettings);
        return Task.CompletedTask;
    }
    Task AgentServices.IAgentProfileService.DeleteProfileAsync(string id, CancellationToken token)
    {
        if (this is IAgentProfileCommandGateway commands) return commands.DeleteProfileAsync(id, token);
        DeleteProfile(id);
        return Task.CompletedTask;
    }
}
