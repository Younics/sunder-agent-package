using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Models;

namespace Sunder.Package.Agent.Subagents.Services;

public sealed class SubagentService(SubagentStore store)
{
    private readonly SubagentStore _store = store;

    public event Action? SubagentsChanged;

    public IReadOnlyList<SubagentRecord> ListSubagents() => _store.List();

    public IReadOnlyList<SubagentRecord> ListUsableSubagents()
        => _store.List().Where(IsUsable).ToArray();

    public SubagentRecord? GetSubagent(string subagentId) => _store.Get(subagentId);

    public SubagentRecord CreateSubagent(string displayName)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new SubagentRecord(
            Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(displayName) ? "New Subagent" : displayName.Trim(),
            null,
            "You are a focused specialist. Complete the delegated task and return only the final result to the parent agent.",
            null,
            null,
            [],
            now,
            now);
        _store.Save(record);
        SubagentsChanged?.Invoke();
        return record;
    }

    public SubagentRecord SaveSubagent(
        string subagentId,
        string displayName,
        string? description,
        string? instructions,
        string? chatProviderId,
        string? chatModelId,
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? selectableCapabilityAssignments,
        string? chatModelSettingsJson = null)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException("Subagent description is required before the subagent can be saved or used.");
        }

        var existing = _store.Get(subagentId) ?? throw new InvalidOperationException($"Subagent '{subagentId}' was not found.");
        var normalizedProviderId = Normalize(chatProviderId);
        var saved = existing with
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Unnamed Subagent" : displayName.Trim(),
            Description = Normalize(description),
            Instructions = Normalize(instructions),
            ChatProviderId = normalizedProviderId,
            ChatModelId = normalizedProviderId is null ? null : Normalize(chatModelId),
            ChatModelSettingsJson = normalizedProviderId is null ? null : Normalize(chatModelSettingsJson),
            SelectableCapabilityAssignments = selectableCapabilityAssignments ?? [],
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        _store.Save(saved);
        SubagentsChanged?.Invoke();
        return saved;
    }

    public void DeleteSubagent(string subagentId)
    {
        _store.Delete(subagentId);
        SubagentsChanged?.Invoke();
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool IsUsable(SubagentRecord subagent)
        => !string.IsNullOrWhiteSpace(subagent.Description);
}
