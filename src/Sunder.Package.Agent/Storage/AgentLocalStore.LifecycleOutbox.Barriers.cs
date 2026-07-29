using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal AgentMemoryConsistencyBarrier? GetMemoryConsistencyBarrier(Guid runId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(session.RootSessionId, session.SessionId),
                   session.WorkspaceId,
                   workspace.CreatedAtUtc
            FROM AgentRuns run
            INNER JOIN AgentSessions session ON session.SessionId = run.SessionId
            LEFT JOIN AgentWorkspaces workspace ON workspace.WorkspaceId = session.WorkspaceId
            WHERE run.RunId = $runId;
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            var rootSessionId = Guid.Parse(reader.GetString(0));
            var workspaceId = reader.IsDBNull(1) ? null : reader.GetString(1);
            var workspaceIncarnationId = workspaceId is not null && !reader.IsDBNull(2)
                ? ComputeLowerHash($"agent-workspace-incarnation-v1\n{workspaceId}\n{reader.GetString(2)}")
                : null;
            var orderingKey = BuildOrderingKey(workspaceId, workspaceIncarnationId, rootSessionId);
            var legacyOrderingKey = BuildLegacyOrderingKey(workspaceId, rootSessionId);
            reader.Close();
            command.CommandText = """
                SELECT EventId, PayloadHash
                FROM AgentLifecycleOutbox
                WHERE EventType = 'TranscriptRolledBack'
                  AND OrderingKey IN ($orderingKey, $legacyOrderingKey)
                ORDER BY Sequence DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$orderingKey", orderingKey);
            command.Parameters.AddWithValue("$legacyOrderingKey", legacyOrderingKey);
            using var outboxReader = command.ExecuteReader();
            if (outboxReader.Read())
            {
                return new AgentMemoryConsistencyBarrier(outboxReader.GetString(0), outboxReader.GetString(1));
            }
        }

        reader.Close();
        command.CommandText = "SELECT MemoryConsistencyBarrierEventId, MemoryConsistencyBarrierPayloadHash FROM AgentRuns WHERE RunId = $runId;";
        using var fallbackReader = command.ExecuteReader();
        return fallbackReader.Read() && !fallbackReader.IsDBNull(0) && !fallbackReader.IsDBNull(1)
            ? new AgentMemoryConsistencyBarrier(fallbackReader.GetString(0), fallbackReader.GetString(1))
            : null;
    }
}
