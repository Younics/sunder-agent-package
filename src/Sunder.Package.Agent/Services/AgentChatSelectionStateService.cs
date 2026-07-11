using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentChatSelectionStateService(IPackageContext packageContext)
{
    private const string SelectedWorkspaceIdKey = "agent.chat.selectedWorkspaceId";
    private const string SelectedSessionIdKey = "agent.chat.selectedSessionId";
    private const string SelectedWorkspaceSessionIdPrefix = "agent.chat.selectedSessionId.";
    private const string SelectedProfileIdKey = "agent.chat.selectedProfileId";

    private readonly IPackageKeyValueStore _state = packageContext.Storage.State;

    public async Task<string?> GetSelectedWorkspaceIdAsync(CancellationToken cancellationToken = default)
        => Normalize(await _state.GetValueAsync(SelectedWorkspaceIdKey, cancellationToken));

    public Task<Guid?> GetSelectedSessionIdAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<Guid?>(null);

    public async Task<Guid?> GetSelectedSessionIdAsync(string? workspaceId, CancellationToken cancellationToken = default)
        => GetWorkspaceSessionKey(workspaceId) is { } key
           && Guid.TryParse(Normalize(await _state.GetValueAsync(key, cancellationToken)), out var sessionId)
            ? sessionId
            : null;

    public async Task<string?> GetSelectedProfileIdAsync(CancellationToken cancellationToken = default)
        => Normalize(await _state.GetValueAsync(SelectedProfileIdKey, cancellationToken));

    public Task SaveSelectedWorkspaceIdAsync(string? workspaceId, CancellationToken cancellationToken = default)
        => SaveOrClearAsync(SelectedWorkspaceIdKey, Normalize(workspaceId), cancellationToken);

    public Task SaveSelectedSessionIdAsync(Guid? sessionId, CancellationToken cancellationToken = default)
        => SaveOrClearAsync(SelectedSessionIdKey, sessionId?.ToString("N"), cancellationToken);

    public Task SaveSelectedSessionIdAsync(
        string? workspaceId,
        Guid? sessionId,
        CancellationToken cancellationToken = default)
    {
        var key = GetWorkspaceSessionKey(workspaceId);
        if (key is null)
        {
            return SaveSelectedSessionIdAsync(sessionId, cancellationToken);
        }

        return SaveOrClearAsync(key, sessionId?.ToString("N"), cancellationToken);
    }

    public Task SaveSelectedProfileIdAsync(string? profileId, CancellationToken cancellationToken = default)
        => SaveOrClearAsync(SelectedProfileIdKey, Normalize(profileId), cancellationToken);

    private async Task SaveOrClearAsync(string key, string? value, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                await _state.DeleteValueAsync(key, cancellationToken);
                return;
            }

            await _state.SetValueAsync(key, value, cancellationToken);
        }
        catch
        {
            // Selection state is a convenience; it must not block chat startup.
        }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? GetWorkspaceSessionKey(string? workspaceId)
    {
        var normalizedWorkspaceId = Normalize(workspaceId);
        return normalizedWorkspaceId is null
            ? null
            : SelectedWorkspaceSessionIdPrefix + normalizedWorkspaceId;
    }
}
