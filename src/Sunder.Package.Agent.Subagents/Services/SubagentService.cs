using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.Runtime;
using System.Runtime.InteropServices;

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
        NotifySubagentsChanged();
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
            throw new SubagentOwnerOperationException(
                "Subagent description is required before the subagent can be saved or used.");
        }

        var existing = _store.Get(subagentId)
            ?? throw new SubagentOwnerOperationException($"Subagent '{subagentId}' was not found.");
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
        NotifySubagentsChanged();
        return saved;
    }

    public void DeleteSubagent(string subagentId)
    {
        _store.Delete(subagentId);
        NotifySubagentsChanged();
    }

    public SubagentRecord ImportSubagent(SubagentRecord record)
        => ImportSubagents([record])[0];

    public IReadOnlyList<SubagentRecord> ImportSubagents(IReadOnlyList<SubagentRecord> records)
    {
        if (records.Count == 0)
        {
            return [];
        }

        var existingById = _store.List().ToDictionary(record => record.SubagentId, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var saved = records.Select(record => record with
        {
            CreatedAtUtc = existingById.GetValueOrDefault(record.SubagentId)?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now,
        })
            .ToArray();
        _store.SaveMany(saved);
        NotifySubagentsChanged();
        return saved;
    }

    public void NotifySubagentsImported()
        => NotifySubagentsChanged();

    private void NotifySubagentsChanged()
    {
        var handlers = SubagentsChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // A listener cannot invalidate an already persisted Subagent mutation.
            }
        }
    }

    private static bool IsFatal(Exception exception)
    {
        if (exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or SEHException)
        {
            return true;
        }

        if (exception is AggregateException aggregate
            && aggregate.InnerExceptions.Any(IsFatal))
        {
            return true;
        }

        return exception.InnerException is { } inner && IsFatal(inner);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool IsUsable(SubagentRecord subagent)
        => !string.IsNullOrWhiteSpace(subagent.Description);
}
