using IExecutionTargetService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Execution_Target.IExecutionTargetService;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Tools.Files;

// The descriptor is the checked binding's policy snapshot; transport and retirement
// remain owned by the generated service reference.
internal sealed record FileExecutionTarget(ServiceReference<IExecutionTargetService> Reference, AgentExecutionTargetDescriptor Descriptor)
{
    internal IExecutionTargetService Service => Reference.Service;

    internal static AgentExecutionTargetContext Context(Guid? sessionId, string? profileId, AgentWorkspaceRecord workspace,
        AgentWorkspaceBindingRecord binding) => new()
    {
        SessionId = sessionId, ProfileId = profileId, Workspace = workspace, Binding = binding,
        AllowOutsideConfiguredScope = false, ApprovedResourceReferences = [], ApprovedResourceClaims = [],
        ApprovedResourceCapabilities = [], ResourceOperation = null, CapturePostMutationResource = false,
        ExpectedConfigurationGeneration = null,
    };
}
