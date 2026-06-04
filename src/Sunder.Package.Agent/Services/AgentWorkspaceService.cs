using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentWorkspaceService
{
    public const string UnassignedSessionsWorkspaceId = AgentLocalStore.UnassignedSessionsWorkspaceId;
    public const string UnassignedSessionsWorkspaceDisplayName = AgentLocalStore.UnassignedSessionsWorkspaceDisplayName;

    private readonly AgentLocalStore _store;
    private readonly IPackageExtensionCatalog? _extensionCatalog;
    private readonly AgentSessionService? _sessionService;
    private bool _isMigratingWorkspacePaths;

    public AgentWorkspaceService(
        AgentLocalStore store,
        IPackageExtensionCatalog? extensionCatalog = null,
        AgentSessionService? sessionService = null)
    {
        _store = store;
        _extensionCatalog = extensionCatalog;
        _sessionService = sessionService;
    }

    public event Action? WorkspacesChanged;

    public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces()
    {
        EnsureLegacyWorkspacePathMigration();
        return _store.ListWorkspaces();
    }

    public AgentWorkspaceRecord? GetWorkspace(string workspaceId)
    {
        EnsureLegacyWorkspacePathMigration();
        return _store.GetWorkspace(workspaceId);
    }

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

    public IReadOnlyList<AgentWorkspacePathRecord> ListWorkspacePaths(string workspaceId)
        => _store.ListWorkspacePaths(workspaceId);

    public void SaveWorkspacePaths(string workspaceId, IReadOnlyList<AgentWorkspacePathRecord> paths)
    {
        SaveWorkspacePathsCore(workspaceId, paths);
        WorkspacesChanged?.Invoke();
    }

    public IReadOnlyList<AgentWorkspaceDocumentRecord> ListWorkspaceDocuments(string workspaceId)
        => _store.ListWorkspaceDocuments(workspaceId);

    public void SaveWorkspaceDocuments(string workspaceId, IReadOnlyList<AgentWorkspaceDocumentRecord> documents)
    {
        SaveWorkspaceDocumentsCore(workspaceId, documents);
        WorkspacesChanged?.Invoke();
    }

    public void DeleteWorkspace(string workspaceId)
    {
        _sessionService?.DeleteSessionsForWorkspace(workspaceId);
        _store.DeleteWorkspace(workspaceId);
        WorkspacesChanged?.Invoke();
    }

    public void ImportWorkspace(
        AgentWorkspaceRecord workspace,
        IReadOnlyList<AgentWorkspacePathRecord>? paths = null,
        IReadOnlyList<AgentWorkspaceDocumentRecord>? documents = null)
    {
        if (string.IsNullOrWhiteSpace(workspace.WorkspaceId))
        {
            throw new InvalidOperationException("Workspace id cannot be empty.");
        }

        var workspaceId = workspace.WorkspaceId.Trim();
        var existing = _store.GetWorkspace(workspaceId);
        var now = DateTimeOffset.UtcNow;
        var imported = new AgentWorkspaceRecord(
            workspaceId,
            string.IsNullOrWhiteSpace(workspace.DisplayName) ? "Imported Workspace" : workspace.DisplayName.Trim(),
            string.IsNullOrWhiteSpace(workspace.Description) ? null : workspace.Description.Trim(),
            existing?.CreatedAtUtc ?? (workspace.CreatedAtUtc == default ? now : workspace.CreatedAtUtc),
            now);

        _store.SaveWorkspace(imported);
        if (paths is not null)
        {
            SaveWorkspacePathsCore(workspaceId, paths);
        }

        if (documents is not null)
        {
            SaveWorkspaceDocumentsCore(workspaceId, documents);
        }

        WorkspacesChanged?.Invoke();
    }

    public void NotifyWorkspacesImported()
        => WorkspacesChanged?.Invoke();

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

    private void EnsureLegacyWorkspacePathMigration()
    {
        if (_isMigratingWorkspacePaths || _extensionCatalog is null)
        {
            return;
        }

        var migrators = _extensionCatalog.GetExtensions(PackageExtensionPoints.WorkspacePathMigrationContributors).ToArray();
        if (migrators.Length == 0)
        {
            return;
        }

        _isMigratingWorkspacePaths = true;
        try
        {
            foreach (var workspace in _store.ListWorkspaces())
            {
                if (workspace.Paths.Count > 0)
                {
                    continue;
                }

                var binding = _store.ListWorkspaceBindings(workspace.WorkspaceId)
                    .FirstOrDefault(item => item.IsEnabled
                                            && string.Equals(item.Role, AgentWorkspaceBindingRoles.PrimaryExecutionTarget, StringComparison.OrdinalIgnoreCase));
                if (binding is null)
                {
                    continue;
                }

                var context = new AgentWorkspacePathMigrationContext(workspace, binding);
                var completedMigrators = new List<IAgentWorkspacePathMigrationContributor>();
                var migrationItems = new List<AgentWorkspacePathMigrationItem>();
                foreach (var migrator in migrators)
                {
                    try
                    {
                        if (!migrator.CanMigrate(context))
                        {
                            continue;
                        }

                        var items = migrator.GetLegacyWorkspacePaths(context)
                            .Where(item => !string.IsNullOrWhiteSpace(item.HostPath))
                            .ToArray();
                        if (items.Length == 0)
                        {
                            continue;
                        }

                        migrationItems.AddRange(items);
                        completedMigrators.Add(migrator);
                    }
                    catch
                    {
                        // Legacy migration should never prevent the workspace list from loading.
                    }
                }

                if (migrationItems.Count == 0)
                {
                    continue;
                }

                SaveWorkspacePathsCore(workspace.WorkspaceId, BuildPathRecords(workspace.WorkspaceId, migrationItems));
                foreach (var migrator in completedMigrators)
                {
                    try
                    {
                        migrator.CompleteWorkspacePathMigration(context);
                    }
                    catch
                    {
                        // A completed path migration is still valid if cleanup of legacy config fails.
                    }
                }
            }
        }
        finally
        {
            _isMigratingWorkspacePaths = false;
        }
    }

    private void SaveWorkspacePathsCore(string workspaceId, IReadOnlyList<AgentWorkspacePathRecord> paths)
        => _store.SaveWorkspacePaths(workspaceId, NormalizePathRecords(workspaceId, paths));

    private void SaveWorkspaceDocumentsCore(string workspaceId, IReadOnlyList<AgentWorkspaceDocumentRecord> documents)
        => _store.SaveWorkspaceDocuments(workspaceId, NormalizeDocumentRecords(workspaceId, documents));

    private static IReadOnlyList<AgentWorkspacePathRecord> BuildPathRecords(
        string workspaceId,
        IReadOnlyList<AgentWorkspacePathMigrationItem> items)
    {
        var now = DateTimeOffset.UtcNow;
        return items
            .OrderBy(item => item.SortOrder)
            .Select((item, index) => new AgentWorkspacePathRecord(
                Guid.NewGuid().ToString("N"),
                workspaceId,
                item.HostPath,
                item.IsDefault,
                index,
                now,
                now))
            .ToArray();
    }

    private static IReadOnlyList<AgentWorkspacePathRecord> NormalizePathRecords(
        string workspaceId,
        IReadOnlyList<AgentWorkspacePathRecord> paths)
    {
        var comparer = GetPathStringComparer();
        var now = DateTimeOffset.UtcNow;
        var normalized = new List<AgentWorkspacePathRecord>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path.HostPath))
            {
                continue;
            }

            var hostPath = Path.GetFullPath(ExpandPath(path.HostPath.Trim()));
            if (normalized.Any(item => comparer.Equals(item.HostPath, hostPath)))
            {
                continue;
            }

            normalized.Add(path with
            {
                PathId = string.IsNullOrWhiteSpace(path.PathId) ? Guid.NewGuid().ToString("N") : path.PathId,
                WorkspaceId = workspaceId,
                HostPath = hostPath,
                SortOrder = normalized.Count,
                CreatedAtUtc = path.CreatedAtUtc == default ? now : path.CreatedAtUtc,
                UpdatedAtUtc = now,
            });
        }

        var defaultIndex = normalized.FindIndex(path => path.IsDefault);
        if (defaultIndex < 0 && normalized.Count > 0)
        {
            defaultIndex = 0;
        }

        for (var index = 0; index < normalized.Count; index++)
        {
            normalized[index] = normalized[index] with
            {
                IsDefault = index == defaultIndex,
                SortOrder = index,
            };
        }

        return normalized;
    }

    private static IReadOnlyList<AgentWorkspaceDocumentRecord> NormalizeDocumentRecords(
        string workspaceId,
        IReadOnlyList<AgentWorkspaceDocumentRecord> documents)
    {
        var comparer = GetPathStringComparer();
        var now = DateTimeOffset.UtcNow;
        var normalized = new List<AgentWorkspaceDocumentRecord>();
        foreach (var document in documents)
        {
            if (string.IsNullOrWhiteSpace(document.FilePath))
            {
                continue;
            }

            var filePath = Path.GetFullPath(ExpandPath(document.FilePath.Trim()));
            if (normalized.Any(item => comparer.Equals(item.FilePath, filePath)))
            {
                continue;
            }

            normalized.Add(document with
            {
                DocumentId = string.IsNullOrWhiteSpace(document.DocumentId) ? Guid.NewGuid().ToString("N") : document.DocumentId,
                WorkspaceId = workspaceId,
                FilePath = filePath,
                SortOrder = normalized.Count,
                CreatedAtUtc = document.CreatedAtUtc == default ? now : document.CreatedAtUtc,
                UpdatedAtUtc = now,
            });
        }

        return normalized;
    }

    private static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : Environment.ExpandEnvironmentVariables(path);
    }

    private static StringComparer GetPathStringComparer()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}

public static class AgentWorkspaceBindingRoles
{
    public const string PrimaryExecutionTarget = "primary-execution-target";
}
