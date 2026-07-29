using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private static AgentPendingPermissionRequestRecord? GetPermissionRequest(
        SqliteConnection connection,
        Guid sessionId,
        string requestId,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {PendingPermissionColumns} FROM AgentPendingPermissionRequests WHERE SessionId = $sessionId AND RequestId = $requestId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        command.Parameters.AddWithValue("$requestId", requestId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPendingPermissionRequest(reader) : null;
    }

    private static AgentPendingPermissionRequestRecord ReadPendingPermissionRequest(SqliteDataReader reader)
        => new AgentPendingPermissionRequestRecord(
            reader.GetString(0), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), Guid.Parse(reader.GetString(5)), reader.GetString(6),
            reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11), reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15), reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17), reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.GetInt64(19) != 0, DateTimeOffset.Parse(reader.GetString(20)),
            reader.IsDBNull(21) ? null : Guid.Parse(reader.GetString(21)),
            reader.IsDBNull(22) ? null : Guid.Parse(reader.GetString(22)),
            Enum.Parse<AgentPendingPermissionStatus>(reader.GetString(23), ignoreCase: true),
            reader.IsDBNull(24) ? null : reader.GetString(24),
            reader.IsDBNull(25) ? null : DateTimeOffset.Parse(reader.GetString(25)),
            reader.IsDBNull(26) ? null : DateTimeOffset.Parse(reader.GetString(26)),
            reader.IsDBNull(27) ? null : reader.GetString(27), reader.GetString(28),
            reader.IsDBNull(29) ? null : reader.GetString(29),
            reader.IsDBNull(30) ? null : DateTimeOffset.Parse(reader.GetString(30)),
            reader.IsDBNull(31) ? null : DateTimeOffset.Parse(reader.GetString(31)),
            reader.IsDBNull(32) ? null : DateTimeOffset.Parse(reader.GetString(32)),
            reader.IsDBNull(33) ? string.Empty : reader.GetString(33))
        {
            ToolExecutionId = reader.IsDBNull(34) ? null : Guid.Parse(reader.GetString(34)),
            ResourceClaimSetVersion = reader.GetInt32(35),
            ResourceClaims = DeserializeResourceClaims(reader.GetString(36)),
        };
}
