using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentChatSelectionStateService(IPackageContext packageContext)
{
    private const string SelectedWorkspaceIdKey = "agent.chat.selectedWorkspaceId";
    private const string SelectedSessionIdKey = "agent.chat.selectedSessionId";
    private const string SelectedProfileIdKey = "agent.chat.selectedProfileId";

    private readonly IPackageKeyValueStore _state = packageContext.Storage.State;

    public string? GetSelectedWorkspaceId()
        => Normalize(_state.GetValue(SelectedWorkspaceIdKey));

    public Guid? GetSelectedSessionId()
        => Guid.TryParse(Normalize(_state.GetValue(SelectedSessionIdKey)), out var sessionId)
            ? sessionId
            : null;

    public string? GetSelectedProfileId()
        => Normalize(_state.GetValue(SelectedProfileIdKey));

    public void SaveSelectedWorkspaceId(string? workspaceId)
        => SaveOrClear(SelectedWorkspaceIdKey, Normalize(workspaceId));

    public void SaveSelectedSessionId(Guid? sessionId)
        => SaveOrClear(SelectedSessionIdKey, sessionId?.ToString("N"));

    public void SaveSelectedProfileId(string? profileId)
        => SaveOrClear(SelectedProfileIdKey, Normalize(profileId));

    private void SaveOrClear(string key, string? value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _state.DeleteValueAsync(key).GetAwaiter().GetResult();
                return;
            }

            _state.SetValueAsync(key, value).GetAwaiter().GetResult();
        }
        catch
        {
            // Selection state is a convenience; it must not block chat startup.
        }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
