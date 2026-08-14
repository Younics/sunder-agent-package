using Sunder.Package.Agent.Shared.Storage;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentChatSelectionStateService
{
    private const string SelectedWorkspaceIdKey = "agent.chat.selectedWorkspaceId";
    private const string SelectedSessionIdKey = "agent.chat.selectedSessionId";
    private const string SelectedProfileIdKey = "agent.chat.selectedProfileId";

    private readonly IPackageKeyValueStore _state;

    public AgentChatSelectionStateService(IPackageContext packageContext)
    {
        _state = packageContext.Storage.State;
    }

    internal event Action<string?>? SelectedWorkspaceChanged;

    public async Task<string?> GetSelectedWorkspaceIdAsync(CancellationToken cancellationToken = default)
    {
        return Normalize(await _state.GetValueAsync(SelectedWorkspaceIdKey, cancellationToken));
    }

    public Task<Guid?> GetSelectedSessionIdAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<Guid?>(null);

    public async Task<Guid?> GetSelectedSessionIdAsync(string? workspaceId, CancellationToken cancellationToken = default)
    {
        return GetWorkspaceSessionKey(workspaceId) is { } key
               && Guid.TryParse(Normalize(await _state.GetValueAsync(key, cancellationToken)), out var sessionId)
            ? sessionId
            : null;
    }

    public async Task<string?> GetSelectedProfileIdAsync(CancellationToken cancellationToken = default)
    {
        return Normalize(await _state.GetValueAsync(SelectedProfileIdKey, cancellationToken));
    }

    public async Task SaveSelectedWorkspaceIdAsync(
        string? workspaceId,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(workspaceId);
        if (!await TrySaveOrClearAsync(SelectedWorkspaceIdKey, normalized, cancellationToken))
        {
            return;
        }
        if (SelectedWorkspaceChanged is not { } changed)
        {
            return;
        }
        foreach (Action<string?> handler in changed.GetInvocationList())
        {
            try
            {
                handler(normalized);
            }
            catch
            {
                // Selection persistence must not fail because a cached view could not refresh.
            }
        }
    }

    public async Task SaveSelectedSessionIdAsync(Guid? sessionId, CancellationToken cancellationToken = default)
    {
        await SaveOrClearAsync(SelectedSessionIdKey, sessionId?.ToString("N"), cancellationToken);
    }

    public async Task SaveSelectedSessionIdAsync(
        string? workspaceId,
        Guid? sessionId,
        CancellationToken cancellationToken = default)
    {
        var key = GetWorkspaceSessionKey(workspaceId);
        if (key is null)
        {
            await SaveOrClearAsync(SelectedSessionIdKey, sessionId?.ToString("N"), cancellationToken);
            return;
        }

        await SaveOrClearAsync(key, sessionId?.ToString("N"), cancellationToken);
    }

    public async Task SaveSelectedProfileIdAsync(string? profileId, CancellationToken cancellationToken = default)
    {
        await SaveOrClearAsync(SelectedProfileIdKey, Normalize(profileId), cancellationToken);
    }

    private async Task SaveOrClearAsync(string key, string? value, CancellationToken cancellationToken)
        => _ = await TrySaveOrClearAsync(key, value, cancellationToken);

    private async Task<bool> TrySaveOrClearAsync(
        string key,
        string? value,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                await _state.DeleteValueAsync(key, cancellationToken);
                return true;
            }

            await _state.SetValueAsync(key, value, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Selection state is a convenience; it must not block chat startup.
            return false;
        }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? GetWorkspaceSessionKey(string? workspaceId)
    {
        var normalizedWorkspaceId = Normalize(workspaceId);
        return normalizedWorkspaceId is null
            ? null
            : CurrentPackageStorageKey.Create("agent.chat.selected-session", 1, normalizedWorkspaceId);
    }
}
