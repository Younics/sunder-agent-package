using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Storage;

namespace Sunder.Package.Agent.Services;

public sealed class AgentWorkspaceService(AgentLocalStore store)
{
    private readonly AgentLocalStore _store = store;

    public event Action? WorkspacesChanged;

    public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces()
        => _store.ListWorkspaces();

    public AgentWorkspaceRecord? GetWorkspace(string workspaceId)
        => _store.GetWorkspace(workspaceId);

    public AgentWorkspaceRecord CreateWorkspace(string displayName)
    {
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord(
            Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(displayName) ? "New Workspace" : displayName.Trim(),
            null,
            now,
            now);

        _store.SaveWorkspace(workspace);
        WorkspacesChanged?.Invoke();
        return workspace;
    }

    public void SaveWorkspace(
        string workspaceId,
        string displayName,
        string? description)
    {
        var existing = _store.GetWorkspace(workspaceId)
            ?? throw new InvalidOperationException($"Workspace '{workspaceId}' was not found.");

        var next = existing with
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Unnamed Workspace" : displayName.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        _store.SaveWorkspace(next);
        WorkspacesChanged?.Invoke();
    }

    public void DeleteWorkspace(string workspaceId)
    {
        _store.DeleteWorkspace(workspaceId);
        WorkspacesChanged?.Invoke();
    }

    public IReadOnlyList<AgentWorkspaceBindingRecord> ListBindings(string workspaceId)
        => _store.ListWorkspaceBindings(workspaceId);

    public AgentWorkspaceBindingRecord? GetBinding(string bindingId)
        => _store.GetWorkspaceBinding(bindingId);

    public AgentWorkspaceBindingRecord SavePrimaryExecutionBinding(
        string workspaceId,
        string contributionId,
        string displayRole = AgentWorkspaceBindingRoles.PrimaryExecutionTarget)
    {
        var existing = _store.ListWorkspaceBindings(workspaceId)
            .FirstOrDefault(binding => string.Equals(binding.Role, displayRole, StringComparison.OrdinalIgnoreCase));
        var now = DateTimeOffset.UtcNow;
        var binding = existing is null
            ? new AgentWorkspaceBindingRecord(
                BuildPrimaryBindingId(workspaceId, displayRole),
                workspaceId,
                PackageExtensionPoints.ExecutionTargets.Id,
                contributionId,
                displayRole,
                IsEnabled: true,
                SortOrder: 0,
                now,
                now)
            : existing with
            {
                ContributionId = contributionId,
                IsEnabled = true,
                UpdatedAtUtc = now,
            };

        _store.SaveWorkspaceBinding(binding);
        WorkspacesChanged?.Invoke();
        return binding;
    }

    public static string BuildPrimaryBindingId(string workspaceId, string role = AgentWorkspaceBindingRoles.PrimaryExecutionTarget)
        => $"{workspaceId}:{role}";

    public void RemovePrimaryExecutionBinding(string workspaceId)
    {
        foreach (var binding in _store.ListWorkspaceBindings(workspaceId)
                     .Where(binding => string.Equals(binding.Role, AgentWorkspaceBindingRoles.PrimaryExecutionTarget, StringComparison.OrdinalIgnoreCase)))
        {
            _store.DeleteWorkspaceBinding(binding.BindingId);
        }

        WorkspacesChanged?.Invoke();
    }
}

public static class AgentWorkspaceBindingRoles
{
    public const string PrimaryExecutionTarget = "primary-execution-target";
}
