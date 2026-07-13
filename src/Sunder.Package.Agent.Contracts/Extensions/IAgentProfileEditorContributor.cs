using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentProfileEditorContributor
{
    string ContributorId { get; }

    bool CanEdit(AgentProfileEditorContext context);

    ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
        AgentProfileEditorContext context,
        CancellationToken cancellationToken = default);

    ValueTask<AgentEditorSaveResult> SaveSectionAsync(
        AgentProfileEditorContext context,
        AgentEditorSaveRequest request,
        CancellationToken cancellationToken = default);
}
