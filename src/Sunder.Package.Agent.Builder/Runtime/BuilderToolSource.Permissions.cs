using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Sunder.Sdk.Services;
using IPermissionSurfaceService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Permission_Surface.IPermissionSurfaceService;

namespace Sunder.Package.Agent.Builder;

[SunderServiceProvider(typeof(IPermissionSurfaceService), ProviderId = "maker.permissions")]
public sealed partial class BuilderToolSource : IPermissionSurfaceService
{
    ValueTask<AgentPermissionSurfaceDescriptor> IPermissionSurfaceService.DescribeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentPermissionSurfaceDescriptor
        {
            SurfaceId = "package-maker", DisplayName = "Package Maker",
            Actions =
            [
                PermissionAction("maker.apply", "Apply Built Package", "Load validated built output or reload through the existing Maker owner; no build or restore.", AgentPermissionDecision.Ask),
                PermissionAction("maker.inspect", "Get Package Info", "Inspect source metadata or recorded outcomes of authorized local workspace package operations.", AgentPermissionDecision.Allow),
                PermissionAction("maker.logs", "Get Package Logs", "Read bounded Runtime/App diagnostics of this Maker-owned development package.", AgentPermissionDecision.Ask),
                PermissionAction("maker.view.inspect", "Inspect Package View", "Read bounded untrusted labels from this exact already open development package view, not the shell or desktop.", AgentPermissionDecision.Ask),
                PermissionAction("maker.view.capture", "Screenshot Package View", "Capture only the exact package view, including its native surfaces.", AgentPermissionDecision.Ask),
                PermissionAction("maker.view.open", "Open Package View", "Explicitly open one view registered by this exact development package.", AgentPermissionDecision.Ask),
                PermissionAction("maker.view.input", "Interact With Package View", "Dispatch one advertised input to a short-lived element of this Maker-owned development view. Application effects may be external; dispatch is not proof of success.", AgentPermissionDecision.Ask),
            ],
        });
    }

    private static AgentPermissionActionDescriptor PermissionAction(string id, string name, string description, AgentPermissionDecision decision) => new()
    {
        ActionId = id, DisplayName = name, Description = description,
        Boundaries = [new() { BoundaryId = "workspace-project", DisplayName = "Selected local workspace projects",
            Description = "Only physically contained project entry points from the selected local workspace roots; never remote execution paths or arbitrary desktop content.", DefaultDecision = decision }],
    };
}
