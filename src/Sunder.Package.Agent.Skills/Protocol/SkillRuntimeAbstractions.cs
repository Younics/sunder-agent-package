using Sunder.Package.Agent.Skills.Services;

namespace Sunder.Package.Agent.Skills.Runtime;

internal interface ISkillManagementGateway
{
    event Action? SkillsChanged;
    Task<IReadOnlyList<InstalledSkillRecord>> ListAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstalledSkillRecord>> ImportGitHubAsync(
        string url,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstalledSkillRecord>> ImportCommonAsync(
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstalledSkillRecord>> ImportLocalAsync(
        string folderPath,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(string skillId, CancellationToken cancellationToken = default);
}

internal interface ISkillOwnerClient
{
    ValueTask<SkillProjection> QueryAsync(CancellationToken cancellationToken = default);
    ValueTask<SkillProjection> CommandAsync(
        SkillCommand command,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<SkillChanged> SubscribeChangesAsync(
        CancellationToken cancellationToken = default);
}
