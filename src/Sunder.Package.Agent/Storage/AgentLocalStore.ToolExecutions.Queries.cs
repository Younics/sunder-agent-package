using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal AgentToolExecutionRecord? GetToolExecution(Guid executionId)
    {
        using var connection = CreateConnection();
        connection.Open();
        return GetToolExecution(connection, transaction: null, executionId);
    }

    internal AgentToolResult? GetToolExecutionResult(Guid executionId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT execution.ToolId,
                   execution.IsReadOnly,
                   execution.OutcomeSummary,
                   item.TextContent,
                   item.ResultSummary,
                   item.StructuredPayloadJson,
                   item.SourcesJson,
                   item.WasTruncated,
                   item.IsError,
                   item.ErrorCode,
                   item.BackendId,
                   item.PresentationPayloadJson
            FROM AgentToolExecutions execution
            INNER JOIN AgentTurnItems item
                ON item.ToolExecutionId = execution.ExecutionId
               AND item.Kind = 'ToolResult'
            WHERE execution.ExecutionId = $executionId
              AND execution.Status IN ('Completed', 'Failed', 'Ambiguous')
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$executionId", executionId.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        IReadOnlyList<AgentToolSourceItem>? sources = null;
        if (!reader.IsDBNull(6))
        {
            try
            {
                sources = JsonSerializer.Deserialize<AgentToolSourceItem[]>(reader.GetString(6));
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Tool execution '{executionId}' has malformed persisted sources.",
                    ex);
            }
        }

        var toolId = reader.GetString(0);
        var summary = reader.IsDBNull(4)
            ? reader.IsDBNull(2)
                ? $"Recorded result for tool '{toolId}'."
                : reader.GetString(2)
            : reader.GetString(4);
        return new AgentToolResult(
            toolId,
            summary,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            sources,
            reader.GetInt64(7) != 0,
            reader.GetInt64(8) != 0,
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11))
        {
            RequiresPromptContextRefresh = reader.GetInt64(1) == 0,
        };
    }

    internal IReadOnlyList<AgentToolExecutionRecord> ListToolExecutions(Guid sessionId)
    {
        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ToolExecutionColumns} FROM AgentToolExecutions WHERE SessionId = $sessionId ORDER BY PreparedAtUtc, ExecutionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        using var reader = command.ExecuteReader();
        var executions = new List<AgentToolExecutionRecord>();
        while (reader.Read())
        {
            executions.Add(ReadToolExecution(reader));
        }
        return executions;
    }
}
