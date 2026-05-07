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

        using var reader = command.ExecuteReader();
        var items = new List<AgentWorkspaceRecord>();
        while (reader.Read())
        {
            items.Add(ReadWorkspace(reader));
        }

        return items;
    }

    private static AgentWorkspaceRecord? GetWorkspace(SqliteConnection connection, string workspaceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT WorkspaceId, DisplayName, Description, CreatedAtUtc, UpdatedAtUtc FROM AgentWorkspaces WHERE WorkspaceId = $workspaceId;";
        command.Parameters.AddWithValue("$workspaceId", workspaceId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadWorkspace(reader) : null;
    }

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
