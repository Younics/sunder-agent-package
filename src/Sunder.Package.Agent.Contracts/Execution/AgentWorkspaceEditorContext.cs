namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Identifies the workspace, selected execution target, and configuration scope presented to workspace-editor contributors.
/// </summary>
/// <remarks>
/// The context can describe a prospective binding before the workspace aggregate is saved. Contributors must therefore treat identifiers as
/// opaque routing input and keep authoritative validation and persistence in Runtime code rather than trusting App-provided editor values.
/// </remarks>
/// <param name="Workspace">The workspace snapshot being edited.</param>
/// <param name="TargetId">The selected <see cref="AgentExecutionTargetDescriptor.TargetId"/> used to select applicable contributors.</param>
/// <param name="ConfigurationId">
/// The opaque binding-scoped configuration key, normally the primary binding identity even when that binding has not yet been persisted.
/// Contributors must not parse this value for authorization decisions.
/// </param>
public sealed record AgentWorkspaceEditorContext(
    AgentWorkspaceRecord Workspace,
    string TargetId,
    string ConfigurationId);
