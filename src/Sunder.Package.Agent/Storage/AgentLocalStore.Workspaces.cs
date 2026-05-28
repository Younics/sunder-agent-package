using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces()
    {
        using var connection = CreateConnection();
        connection.Open();
        return ListWorkspaces(connection);
    }

    public AgentWorkspaceRecord? GetWorkspace(string workspaceId)
    {
        using var connection = CreateConnection();
        connection.Open();
        return GetWorkspace(connection, workspaceId);
    }

    public void SaveWorkspace(AgentWorkspaceRecord workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace.WorkspaceId))
        {
            throw new InvalidOperationException("Workspace id cannot be empty.");
        }

        using var connection = CreateConnection();
        connection.Open();
        InsertOrReplaceWorkspace(connection, workspace);
    }

    public void DeleteWorkspace(string workspaceId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using (var deleteDocumentsCommand = connection.CreateCommand())
        {
            deleteDocumentsCommand.Transaction = transaction;
            deleteDocumentsCommand.CommandText = "DELETE FROM AgentWorkspaceDocuments WHERE WorkspaceId = $workspaceId;";
            deleteDocumentsCommand.Parameters.AddWithValue("$workspaceId", workspaceId);
            deleteDocumentsCommand.ExecuteNonQuery();
        }

        using (var deletePathsCommand = connection.CreateCommand())
        {
            deletePathsCommand.Transaction = transaction;
            deletePathsCommand.CommandText = "DELETE FROM AgentWorkspacePaths WHERE WorkspaceId = $workspaceId;";
            deletePathsCommand.Parameters.AddWithValue("$workspaceId", workspaceId);
            deletePathsCommand.ExecuteNonQuery();
        }

        using (var deleteBindingsCommand = connection.CreateCommand())
        {
            deleteBindingsCommand.Transaction = transaction;
            deleteBindingsCommand.CommandText = "DELETE FROM AgentWorkspaceBindings WHERE WorkspaceId = $workspaceId;";
            deleteBindingsCommand.Parameters.AddWithValue("$workspaceId", workspaceId);
            deleteBindingsCommand.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM AgentWorkspaces WHERE WorkspaceId = $workspaceId;";
            command.Parameters.AddWithValue("$workspaceId", workspaceId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<AgentWorkspacePathRecord> ListWorkspacePaths(string workspaceId)
    {
        using var connection = CreateConnection();
        connection.Open();
        return ListWorkspacePaths(connection, workspaceId);
    }

    public void SaveWorkspacePaths(string workspaceId, IReadOnlyList<AgentWorkspacePathRecord> paths)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM AgentWorkspacePaths WHERE WorkspaceId = $workspaceId;";
            deleteCommand.Parameters.AddWithValue("$workspaceId", workspaceId);
            deleteCommand.ExecuteNonQuery();
        }

        foreach (var path in paths)
        {
            InsertWorkspacePath(connection, path, transaction);
        }

        transaction.Commit();
    }

    public IReadOnlyList<AgentWorkspaceDocumentRecord> ListWorkspaceDocuments(string workspaceId)
    {
        using var connection = CreateConnection();
        connection.Open();
        return ListWorkspaceDocuments(connection, workspaceId);
    }

    public void SaveWorkspaceDocuments(string workspaceId, IReadOnlyList<AgentWorkspaceDocumentRecord> documents)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM AgentWorkspaceDocuments WHERE WorkspaceId = $workspaceId;";
            deleteCommand.Parameters.AddWithValue("$workspaceId", workspaceId);
            deleteCommand.ExecuteNonQuery();
        }

        foreach (var document in documents)
        {
            InsertWorkspaceDocument(connection, document, transaction);
        }

        transaction.Commit();
    }

    public IReadOnlyList<AgentWorkspaceBindingRecord> ListWorkspaceBindings(string workspaceId)
    {
        using var connection = CreateConnection();
        connection.Open();
        return ListWorkspaceBindings(connection, workspaceId);
    }

    public AgentWorkspaceBindingRecord? GetWorkspaceBinding(string bindingId)
    {
        using var connection = CreateConnection();
        connection.Open();
        return GetWorkspaceBinding(connection, bindingId);
    }

    public void SaveWorkspaceBinding(AgentWorkspaceBindingRecord binding)
    {
        using var connection = CreateConnection();
        connection.Open();
        InsertOrReplaceWorkspaceBinding(connection, binding);
    }

    public void DeleteWorkspaceBinding(string bindingId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AgentWorkspaceBindings WHERE BindingId = $bindingId;";
        command.Parameters.AddWithValue("$bindingId", bindingId);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT WorkspaceId, DisplayName, Description, CreatedAtUtc, UpdatedAtUtc FROM AgentWorkspaces ORDER BY DisplayName, UpdatedAtUtc DESC;";

        var items = new List<AgentWorkspaceRecord>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                items.Add(ReadWorkspace(reader));
            }
        }

        return items.Select(workspace => HydrateWorkspace(connection, workspace)).ToArray();
    }

    private static AgentWorkspaceRecord? GetWorkspace(SqliteConnection connection, string workspaceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT WorkspaceId, DisplayName, Description, CreatedAtUtc, UpdatedAtUtc FROM AgentWorkspaces WHERE WorkspaceId = $workspaceId;";
        command.Parameters.AddWithValue("$workspaceId", workspaceId);

        AgentWorkspaceRecord? workspace = null;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                workspace = ReadWorkspace(reader);
            }
        }

        return workspace is null ? null : HydrateWorkspace(connection, workspace);
    }

    private static AgentWorkspaceRecord HydrateWorkspace(SqliteConnection connection, AgentWorkspaceRecord workspace)
        => workspace with
        {
            Paths = ListWorkspacePaths(connection, workspace.WorkspaceId),
            Documents = ListWorkspaceDocuments(connection, workspace.WorkspaceId),
        };

    private static AgentWorkspaceRecord ReadWorkspace(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4)));

    private static void InsertOrReplaceWorkspace(SqliteConnection connection, AgentWorkspaceRecord workspace, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AgentWorkspaces (WorkspaceId, DisplayName, Description, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($workspaceId, $displayName, $description, $createdAtUtc, $updatedAtUtc)
            ON CONFLICT(WorkspaceId) DO UPDATE SET
                DisplayName = excluded.DisplayName,
                Description = excluded.Description,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$workspaceId", workspace.WorkspaceId);
        command.Parameters.AddWithValue("$displayName", workspace.DisplayName);
        command.Parameters.AddWithValue("$description", (object?)workspace.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", workspace.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", workspace.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<AgentWorkspacePathRecord> ListWorkspacePaths(
        SqliteConnection connection,
        string workspaceId,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT PathId, WorkspaceId, HostPath, IsDefault, SortOrder, CreatedAtUtc, UpdatedAtUtc FROM AgentWorkspacePaths WHERE WorkspaceId = $workspaceId ORDER BY SortOrder, HostPath;";
        command.Parameters.AddWithValue("$workspaceId", workspaceId);

        using var reader = command.ExecuteReader();
        var paths = new List<AgentWorkspacePathRecord>();
        while (reader.Read())
        {
            paths.Add(new AgentWorkspacePathRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0,
                reader.GetInt32(4),
                DateTimeOffset.Parse(reader.GetString(5)),
                DateTimeOffset.Parse(reader.GetString(6))));
        }

        return paths;
    }

    private static void InsertWorkspacePath(
        SqliteConnection connection,
        AgentWorkspacePathRecord path,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AgentWorkspacePaths (PathId, WorkspaceId, HostPath, IsDefault, SortOrder, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($pathId, $workspaceId, $hostPath, $isDefault, $sortOrder, $createdAtUtc, $updatedAtUtc);
            """;
        command.Parameters.AddWithValue("$pathId", path.PathId);
        command.Parameters.AddWithValue("$workspaceId", path.WorkspaceId);
        command.Parameters.AddWithValue("$hostPath", path.HostPath);
        command.Parameters.AddWithValue("$isDefault", path.IsDefault ? 1 : 0);
        command.Parameters.AddWithValue("$sortOrder", path.SortOrder);
        command.Parameters.AddWithValue("$createdAtUtc", path.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", path.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<AgentWorkspaceDocumentRecord> ListWorkspaceDocuments(
        SqliteConnection connection,
        string workspaceId,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DocumentId, WorkspaceId, FilePath, SortOrder, CreatedAtUtc, UpdatedAtUtc FROM AgentWorkspaceDocuments WHERE WorkspaceId = $workspaceId ORDER BY SortOrder, FilePath;";
        command.Parameters.AddWithValue("$workspaceId", workspaceId);

        using var reader = command.ExecuteReader();
        var documents = new List<AgentWorkspaceDocumentRecord>();
        while (reader.Read())
        {
            documents.Add(new AgentWorkspaceDocumentRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                DateTimeOffset.Parse(reader.GetString(5))));
        }

        return documents;
    }

    private static void InsertWorkspaceDocument(
        SqliteConnection connection,
        AgentWorkspaceDocumentRecord document,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AgentWorkspaceDocuments (DocumentId, WorkspaceId, FilePath, SortOrder, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($documentId, $workspaceId, $filePath, $sortOrder, $createdAtUtc, $updatedAtUtc);
            """;
        command.Parameters.AddWithValue("$documentId", document.DocumentId);
        command.Parameters.AddWithValue("$workspaceId", document.WorkspaceId);
        command.Parameters.AddWithValue("$filePath", document.FilePath);
        command.Parameters.AddWithValue("$sortOrder", document.SortOrder);
        command.Parameters.AddWithValue("$createdAtUtc", document.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", document.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<AgentWorkspaceBindingRecord> ListWorkspaceBindings(
        SqliteConnection connection,
        string workspaceId,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT BindingId, WorkspaceId, ExtensionPointId, ContributionId, Role, IsEnabled, SortOrder, CreatedAtUtc, UpdatedAtUtc FROM AgentWorkspaceBindings WHERE WorkspaceId = $workspaceId ORDER BY SortOrder, ContributionId;";
        command.Parameters.AddWithValue("$workspaceId", workspaceId);

        using var reader = command.ExecuteReader();
        var bindings = new List<AgentWorkspaceBindingRecord>();
        while (reader.Read())
        {
            bindings.Add(ReadWorkspaceBinding(reader));
        }

        return bindings;
    }

    private static AgentWorkspaceBindingRecord? GetWorkspaceBinding(SqliteConnection connection, string bindingId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT BindingId, WorkspaceId, ExtensionPointId, ContributionId, Role, IsEnabled, SortOrder, CreatedAtUtc, UpdatedAtUtc FROM AgentWorkspaceBindings WHERE BindingId = $bindingId;";
        command.Parameters.AddWithValue("$bindingId", bindingId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadWorkspaceBinding(reader) : null;
    }

    private static AgentWorkspaceBindingRecord ReadWorkspaceBinding(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5) != 0,
            reader.GetInt32(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            DateTimeOffset.Parse(reader.GetString(8)));

    private static void InsertOrReplaceWorkspaceBinding(
        SqliteConnection connection,
        AgentWorkspaceBindingRecord binding,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AgentWorkspaceBindings (BindingId, WorkspaceId, ExtensionPointId, ContributionId, Role, IsEnabled, SortOrder, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($bindingId, $workspaceId, $extensionPointId, $contributionId, $role, $isEnabled, $sortOrder, $createdAtUtc, $updatedAtUtc)
            ON CONFLICT(BindingId) DO UPDATE SET
                WorkspaceId = excluded.WorkspaceId,
                ExtensionPointId = excluded.ExtensionPointId,
                ContributionId = excluded.ContributionId,
                Role = excluded.Role,
                IsEnabled = excluded.IsEnabled,
                SortOrder = excluded.SortOrder,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$bindingId", binding.BindingId);
        command.Parameters.AddWithValue("$workspaceId", binding.WorkspaceId);
        command.Parameters.AddWithValue("$extensionPointId", binding.ExtensionPointId);
        command.Parameters.AddWithValue("$contributionId", binding.ContributionId);
        command.Parameters.AddWithValue("$role", binding.Role);
        command.Parameters.AddWithValue("$isEnabled", binding.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$sortOrder", binding.SortOrder);
        command.Parameters.AddWithValue("$createdAtUtc", binding.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", binding.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }
}
