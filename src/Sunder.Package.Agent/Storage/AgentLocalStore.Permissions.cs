using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    public IReadOnlyList<AgentPermissionOverride> ListPermissionOverrides()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ActionId, BoundaryId, Decision, UpdatedAtUtc FROM AgentPermissionOverrides ORDER BY ActionId, BoundaryId;";

        using var reader = command.ExecuteReader();
        var overrides = new List<AgentPermissionOverride>();
        while (reader.Read())
        {
            overrides.Add(new AgentPermissionOverride(
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<AgentPermissionDecision>(reader.GetString(2), ignoreCase: true),
                DateTimeOffset.Parse(reader.GetString(3))));
        }

        return overrides;
    }

    public void SavePermissionOverride(AgentPermissionOverride permissionOverride)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AgentPermissionOverrides (ActionId, BoundaryId, Decision, UpdatedAtUtc)
            VALUES ($actionId, $boundaryId, $decision, $updatedAtUtc)
            ON CONFLICT(ActionId, BoundaryId) DO UPDATE SET
                Decision = excluded.Decision,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$actionId", permissionOverride.ActionId);
        command.Parameters.AddWithValue("$boundaryId", permissionOverride.BoundaryId);
        command.Parameters.AddWithValue("$decision", permissionOverride.Decision.ToString());
        command.Parameters.AddWithValue("$updatedAtUtc", permissionOverride.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void DeletePermissionOverride(string actionId, string boundaryId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AgentPermissionOverrides WHERE ActionId = $actionId AND BoundaryId = $boundaryId;";
        command.Parameters.AddWithValue("$actionId", actionId);
        command.Parameters.AddWithValue("$boundaryId", boundaryId);
        command.ExecuteNonQuery();
    }

    public AgentSessionPermissionState GetSessionPermissionState(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT IsUnrestrictedModeEnabled FROM AgentSessionPermissionStates WHERE SessionId = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        var value = command.ExecuteScalar();
        return new AgentSessionPermissionState(sessionId, value is not null && Convert.ToInt64(value) != 0);
    }

    public void SetSessionUnrestrictedMode(Guid sessionId, bool isEnabled)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AgentSessionPermissionStates (SessionId, IsUnrestrictedModeEnabled)
            VALUES ($sessionId, $isEnabled)
            ON CONFLICT(SessionId) DO UPDATE SET IsUnrestrictedModeEnabled = excluded.IsUnrestrictedModeEnabled;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$isEnabled", isEnabled ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AgentSessionPermissionApproval> ListSessionPermissionApprovals(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ApprovalId, SessionId, ActionId, MatcherKind, Pattern, CreatedAtUtc FROM AgentSessionPermissionApprovals WHERE SessionId = $sessionId ORDER BY CreatedAtUtc DESC;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());

        using var reader = command.ExecuteReader();
        var approvals = new List<AgentSessionPermissionApproval>();
        while (reader.Read())
        {
            approvals.Add(new AgentSessionPermissionApproval(
                reader.GetString(0),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                Enum.Parse<AgentPermissionMatcherKind>(reader.GetString(3), ignoreCase: true),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5))));
        }

        return approvals;
    }

    public void SaveSessionPermissionApproval(AgentSessionPermissionApproval approval)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO AgentSessionPermissionApprovals (ApprovalId, SessionId, ActionId, MatcherKind, Pattern, CreatedAtUtc) VALUES ($approvalId, $sessionId, $actionId, $matcherKind, $pattern, $createdAtUtc);";
        command.Parameters.AddWithValue("$approvalId", approval.ApprovalId);
        command.Parameters.AddWithValue("$sessionId", approval.SessionId.ToString());
        command.Parameters.AddWithValue("$actionId", approval.ActionId);
        command.Parameters.AddWithValue("$matcherKind", approval.MatcherKind.ToString());
        command.Parameters.AddWithValue("$pattern", approval.Pattern);
        command.Parameters.AddWithValue("$createdAtUtc", approval.CreatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    public AgentPendingPermissionRequestRecord SavePendingPermissionRequest(AgentPendingPermissionRequestRecord record)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AgentPendingPermissionRequests (RequestId, SessionId, RunId, RunRevision, ProfileId, UserTurnId, UserMessage, CallId, ActionId, BoundaryId, Summary, ToolId, ArgumentsJson, Command, Path, WorkspaceId, BindingId, ResourceDisplayName, ResourceReference, IsMutation, CreatedAtUtc, ParentSessionId, RootSessionId)
            VALUES ($requestId, $sessionId, $runId, $runRevision, $profileId, $userTurnId, $userMessage, $callId, $actionId, $boundaryId, $summary, $toolId, $argumentsJson, $command, $path, $workspaceId, $bindingId, $resourceDisplayName, $resourceReference, $isMutation, $createdAtUtc, $parentSessionId, $rootSessionId);
            """;
        command.Parameters.AddWithValue("$requestId", record.RequestId);
        command.Parameters.AddWithValue("$sessionId", record.SessionId.ToString());
        command.Parameters.AddWithValue("$runId", record.RunId.ToString());
        command.Parameters.AddWithValue("$runRevision", record.RunRevision);
        command.Parameters.AddWithValue("$profileId", (object?)record.ProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$userTurnId", record.UserTurnId.ToString());
        command.Parameters.AddWithValue("$userMessage", record.UserMessage);
        command.Parameters.AddWithValue("$callId", record.CallId);
        command.Parameters.AddWithValue("$actionId", record.ActionId);
        command.Parameters.AddWithValue("$boundaryId", record.BoundaryId);
        command.Parameters.AddWithValue("$summary", record.Summary);
        command.Parameters.AddWithValue("$toolId", (object?)record.ToolId ?? DBNull.Value);
        command.Parameters.AddWithValue("$argumentsJson", record.ArgumentsJson);
        command.Parameters.AddWithValue("$command", (object?)record.Command ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", (object?)record.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$workspaceId", (object?)record.WorkspaceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$bindingId", (object?)record.BindingId ?? DBNull.Value);
        command.Parameters.AddWithValue("$resourceDisplayName", (object?)record.ResourceDisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$resourceReference", (object?)record.ResourceReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$isMutation", record.IsMutation ? 1 : 0);
        command.Parameters.AddWithValue("$createdAtUtc", record.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$parentSessionId", record.ParentSessionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$rootSessionId", record.RootSessionId?.ToString() ?? (object)DBNull.Value);
        command.ExecuteNonQuery();
        return record;
    }

    public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingPermissionRequests(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT RequestId, SessionId, RunId, RunRevision, ProfileId, UserTurnId, UserMessage, CallId, ActionId, BoundaryId, Summary, ToolId, ArgumentsJson, Command, Path, WorkspaceId, BindingId, ResourceDisplayName, ResourceReference, IsMutation, CreatedAtUtc, ParentSessionId, RootSessionId FROM AgentPendingPermissionRequests WHERE SessionId = $sessionId ORDER BY CreatedAtUtc DESC;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());

        using var reader = command.ExecuteReader();
        var requests = new List<AgentPendingPermissionRequestRecord>();
        while (reader.Read())
        {
            requests.Add(ReadPendingPermissionRequest(reader));
        }

        return requests;
    }

    public IReadOnlyList<AgentPendingPermissionRequestRecord> ListPendingPermissionRequestsForSessionTree(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();

        var session = GetSession(sessionId);
        var rootSessionId = session?.RootSessionId ?? sessionId;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT RequestId, SessionId, RunId, RunRevision, ProfileId, UserTurnId, UserMessage, CallId, ActionId, BoundaryId, Summary, ToolId, ArgumentsJson, Command, Path, WorkspaceId, BindingId, ResourceDisplayName, ResourceReference, IsMutation, CreatedAtUtc, ParentSessionId, RootSessionId FROM AgentPendingPermissionRequests WHERE SessionId = $sessionId OR RootSessionId = $rootSessionId OR ParentSessionId = $sessionId ORDER BY CreatedAtUtc DESC;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$rootSessionId", rootSessionId.ToString());

        using var reader = command.ExecuteReader();
        var requests = new List<AgentPendingPermissionRequestRecord>();
        while (reader.Read())
        {
            requests.Add(ReadPendingPermissionRequest(reader));
        }

        return requests;
    }

    public AgentPendingPermissionRequestRecord? GetPendingPermissionRequest(Guid sessionId, string requestId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT RequestId, SessionId, RunId, RunRevision, ProfileId, UserTurnId, UserMessage, CallId, ActionId, BoundaryId, Summary, ToolId, ArgumentsJson, Command, Path, WorkspaceId, BindingId, ResourceDisplayName, ResourceReference, IsMutation, CreatedAtUtc, ParentSessionId, RootSessionId FROM AgentPendingPermissionRequests WHERE SessionId = $sessionId AND RequestId = $requestId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$requestId", requestId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPendingPermissionRequest(reader) : null;
    }

    public void DeletePendingPermissionRequest(Guid sessionId, string requestId)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AgentPendingPermissionRequests WHERE SessionId = $sessionId AND RequestId = $requestId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$requestId", requestId);
        command.ExecuteNonQuery();
    }

    private static AgentPendingPermissionRequestRecord ReadPendingPermissionRequest(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            Guid.Parse(reader.GetString(5)),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.GetInt64(19) != 0,
            DateTimeOffset.Parse(reader.GetString(20)),
            reader.IsDBNull(21) ? null : Guid.Parse(reader.GetString(21)),
            reader.IsDBNull(22) ? null : Guid.Parse(reader.GetString(22)));
}
