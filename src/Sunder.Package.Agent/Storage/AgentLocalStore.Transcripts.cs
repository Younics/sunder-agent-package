using Microsoft.Data.Sqlite;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    public AgentTranscriptMessageRecord AppendMessage(Guid sessionId, AgentMessageRole role, string content)
    {
        return ProjectTurnToTranscriptMessage(AppendTextTurn(sessionId, role, content));
    }

    public AgentTurnRecord AppendTextTurn(Guid sessionId, AgentMessageRole role, string content)
    {
        var now = DateTimeOffset.UtcNow;
        var turn = CreateTextTurn(Guid.NewGuid(), sessionId, role, AgentTurnKind.Message, content, now, now);
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        InsertTurn(connection, transaction, turn);
        TouchSession(connection, sessionId, null, null, transaction);
        transaction.Commit();
        return turn;
    }

    public AgentTurnRecord AppendUserTurn(Guid sessionId, AgentMessageRole role, string content, IReadOnlyList<AgentStoredAttachment> attachments)
    {
        var now = DateTimeOffset.UtcNow;
        var turn = CreateMessageTurn(Guid.NewGuid(), sessionId, role, content, attachments, now, now);
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        InsertTurn(connection, transaction, turn);
        TouchSession(connection, sessionId, null, null, transaction);
        transaction.Commit();
        return turn;
    }

    public AgentTranscriptMessageRecord UpdateMessageContent(Guid messageId, string content)
    {
        return ProjectTurnToTranscriptMessage(UpdateTextTurn(messageId, content));
    }

    public AgentTurnRecord UpdateTextTurn(Guid messageId, string content)
    {
        using var connection = CreateConnection();
        connection.Open();

        var existingTurn = GetTurn(connection, messageId) ?? throw new InvalidOperationException($"Message '{messageId}' was not found.");
        if (!CanUpdateProjectedMessage(existingTurn))
        {
            throw new InvalidOperationException($"Turn '{messageId}' does not support in-place text updates.");
        }

        var updatedAtUtc = DateTimeOffset.UtcNow;
        using var transaction = connection.BeginTransaction();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE AgentTurns SET UpdatedAtUtc = $updatedAtUtc WHERE TurnId = $id;";
        command.Parameters.AddWithValue("$updatedAtUtc", updatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", messageId.ToString());
        command.ExecuteNonQuery();

        using var updateItem = connection.CreateCommand();
        updateItem.Transaction = transaction;
        updateItem.CommandText = "UPDATE AgentTurnItems SET TextContent = $content WHERE TurnId = $turnId AND SequenceNumber = 0;";
        updateItem.Parameters.AddWithValue("$content", content);
        updateItem.Parameters.AddWithValue("$turnId", messageId.ToString());
        updateItem.ExecuteNonQuery();

        TouchSession(connection, existingTurn.SessionId, null, null, transaction);
        transaction.Commit();

        return GetTurn(connection, messageId) ?? throw new InvalidOperationException($"Turn '{messageId}' was not found after update.");
    }

    public AgentTurnRecord AppendToolCallTurn(
        Guid sessionId,
        AgentMessageRole role,
        string callId,
        string toolId,
        string argumentsJson)
    {
        var now = DateTimeOffset.UtcNow;
        var turn = CreateToolCallTurn(Guid.NewGuid(), sessionId, role, callId, toolId, argumentsJson, now, now);
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        InsertTurn(connection, transaction, turn);
        TouchSession(connection, sessionId, null, null, transaction);
        transaction.Commit();
        return turn;
    }

    public AgentTurnRecord AppendToolResultTurn(
        Guid sessionId,
        string callId,
        string toolId,
        string? argumentsJson,
        string? content,
        string? resultSummary,
        string? structuredPayloadJson,
        string? sourcesJson,
        bool wasTruncated,
        bool isError,
        string? errorCode,
        string? backendId)
    {
        var now = DateTimeOffset.UtcNow;
        var turn = CreateToolResultTurn(
            Guid.NewGuid(),
            sessionId,
            callId,
            toolId,
            argumentsJson,
            content,
            resultSummary,
            structuredPayloadJson,
            sourcesJson,
            wasTruncated,
            isError,
            errorCode,
            backendId,
            now,
            now);
        using var connection = CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        InsertTurn(connection, transaction, turn);
        TouchSession(connection, sessionId, null, null, transaction);
        transaction.Commit();
        return turn;
    }

    private static AgentTurnRecord CreateTextTurn(
        Guid turnId,
        Guid sessionId,
        AgentMessageRole role,
        AgentTurnKind kind,
        string content,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
        => new(
            turnId,
            sessionId,
            role,
            kind,
            [
                new AgentTurnItemRecord(
                    turnId,
                    turnId,
                    0,
                    AgentTurnItemKind.Text,
                    content,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    false,
                    null,
                    null)
            ],
            createdAtUtc,
            updatedAtUtc);

    private static AgentTurnRecord CreateMessageTurn(
        Guid turnId,
        Guid sessionId,
        AgentMessageRole role,
        string content,
        IReadOnlyList<AgentStoredAttachment> attachments,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        var items = new List<AgentTurnItemRecord>();
        var sequenceNumber = 0;
        if (!string.IsNullOrWhiteSpace(content))
        {
            items.Add(new AgentTurnItemRecord(
                Guid.NewGuid(),
                turnId,
                sequenceNumber++,
                AgentTurnItemKind.Text,
                content,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                false,
                null,
                null));
        }

        foreach (var attachment in attachments)
        {
            items.Add(new AgentTurnItemRecord(
                Guid.NewGuid(),
                turnId,
                sequenceNumber++,
                AgentTurnItemKind.Attachment,
                attachment.TextContent,
                null,
                null,
                null,
                null,
                JsonSerializer.Serialize(attachment.Metadata),
                null,
                attachment.Metadata.WasTruncated,
                false,
                null,
                attachment.Metadata.AttachmentId.ToString("N")));
        }

        return new AgentTurnRecord(
            turnId,
            sessionId,
            role,
            AgentTurnKind.Message,
            items,
            createdAtUtc,
            updatedAtUtc);
    }

    private static AgentTurnRecord CreateToolCallTurn(
        Guid turnId,
        Guid sessionId,
        AgentMessageRole role,
        string callId,
        string toolId,
        string argumentsJson,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
        => new(
            turnId,
            sessionId,
            role,
            AgentTurnKind.ToolCall,
            [
                new AgentTurnItemRecord(
                    Guid.NewGuid(),
                    turnId,
                    0,
                    AgentTurnItemKind.ToolCall,
                    null,
                    callId,
                    toolId,
                    argumentsJson,
                    null,
                    null,
                    null,
                    false,
                    false,
                    null,
                    null)
            ],
            createdAtUtc,
            updatedAtUtc);

    private static AgentTurnRecord CreateToolResultTurn(
        Guid turnId,
        Guid sessionId,
        string callId,
        string toolId,
        string? argumentsJson,
        string? content,
        string? resultSummary,
        string? structuredPayloadJson,
        string? sourcesJson,
        bool wasTruncated,
        bool isError,
        string? errorCode,
        string? backendId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
        => new(
            turnId,
            sessionId,
            AgentMessageRole.Tool,
            AgentTurnKind.ToolResult,
            [
                new AgentTurnItemRecord(
                    Guid.NewGuid(),
                    turnId,
                    0,
                    AgentTurnItemKind.ToolResult,
                    content,
                    callId,
                    toolId,
                    argumentsJson,
                    resultSummary,
                    structuredPayloadJson,
                    sourcesJson,
                    wasTruncated,
                    isError,
                    errorCode,
                    backendId)
            ],
            createdAtUtc,
            updatedAtUtc);

    private static bool CanUpdateProjectedMessage(AgentTurnRecord turn)
        => turn.Kind == AgentTurnKind.Message
           && turn.Items.Count == 1
           && turn.Items[0].Kind == AgentTurnItemKind.Text;

    private static AgentTranscriptMessageRecord ProjectTurnToTranscriptMessage(AgentTurnRecord turn)
        => new(
            turn.TurnId,
            turn.SessionId,
            turn.Role,
            RenderTurnContent(turn),
            turn.CreatedAtUtc);

    private static string RenderTurnContent(AgentTurnRecord turn)
    {
        var parts = new List<string>();
        foreach (var item in turn.Items.OrderBy(item => item.SequenceNumber))
        {
            switch (item.Kind)
            {
                case AgentTurnItemKind.Text when !string.IsNullOrWhiteSpace(item.TextContent):
                    parts.Add(item.TextContent.Trim());
                    break;

                case AgentTurnItemKind.ToolCall:
                    parts.Add(RenderToolCallItem(item));
                    break;

                case AgentTurnItemKind.ToolResult:
                    parts.Add(RenderToolResultItem(item));
                    break;

                case AgentTurnItemKind.Attachment:
                    parts.Add(RenderAttachmentItem(item));
                    break;
            }
        }

        return string.Join("\n\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string RenderToolCallItem(AgentTurnItemRecord item)
    {
        var toolId = string.IsNullOrWhiteSpace(item.ToolId) ? "unknown_tool" : item.ToolId;
        if (string.IsNullOrWhiteSpace(item.ArgumentsJson))
        {
            return $"Tool call: {toolId}";
        }

        return $"Tool call: {toolId}\n```json\n{item.ArgumentsJson}\n```";
    }

    private static string RenderToolResultItem(AgentTurnItemRecord item)
    {
        if (!string.IsNullOrWhiteSpace(item.TextContent))
        {
            return item.TextContent.Trim();
        }

        return string.IsNullOrWhiteSpace(item.ResultSummary)
            ? "Tool result."
            : item.ResultSummary;
    }

    private static string RenderAttachmentItem(AgentTurnItemRecord item)
    {
        var metadata = TryReadAttachmentMetadata(item);
        if (metadata is null)
        {
            return "Attachment.";
        }

        var text = $"Attachment: {metadata.FileName} ({metadata.MediaType}, {metadata.SizeBytes} bytes)";
        return metadata.WasTruncated ? text + "\nText content was truncated." : text;
    }

    private static AgentAttachmentMetadata? TryReadAttachmentMetadata(AgentTurnItemRecord item)
    {
        if (string.IsNullOrWhiteSpace(item.StructuredPayloadJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentAttachmentMetadata>(item.StructuredPayloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void InsertTurn(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        AgentTurnRecord turn,
        bool ignoreConflicts = false)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT {(ignoreConflicts ? "OR IGNORE " : string.Empty)}INTO AgentTurns (TurnId, SessionId, Role, Kind, CreatedAtUtc, UpdatedAtUtc) VALUES ($id, $sessionId, $role, $kind, $created, $updated);";
        command.Parameters.AddWithValue("$id", turn.TurnId.ToString());
        command.Parameters.AddWithValue("$sessionId", turn.SessionId.ToString());
        command.Parameters.AddWithValue("$role", turn.Role.ToString());
        command.Parameters.AddWithValue("$kind", turn.Kind.ToString());
        command.Parameters.AddWithValue("$created", turn.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", turn.UpdatedAtUtc.ToString("O"));
        command.ExecuteNonQuery();

        foreach (var item in turn.Items.OrderBy(item => item.SequenceNumber))
        {
            InsertTurnItem(connection, transaction, item, ignoreConflicts);
        }
    }

    private static void InsertTurnItem(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        AgentTurnItemRecord item,
        bool ignoreConflicts)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT {(ignoreConflicts ? "OR IGNORE " : string.Empty)}INTO AgentTurnItems (ItemId, TurnId, SequenceNumber, Kind, TextContent, CallId, ToolId, ArgumentsJson, ResultSummary, StructuredPayloadJson, SourcesJson, WasTruncated, IsError, ErrorCode, BackendId) VALUES ($itemId, $turnId, $sequenceNumber, $kind, $textContent, $callId, $toolId, $argumentsJson, $resultSummary, $structuredPayloadJson, $sourcesJson, $wasTruncated, $isError, $errorCode, $backendId);";
        command.Parameters.AddWithValue("$itemId", item.ItemId.ToString());
        command.Parameters.AddWithValue("$turnId", item.TurnId.ToString());
        command.Parameters.AddWithValue("$sequenceNumber", item.SequenceNumber);
        command.Parameters.AddWithValue("$kind", item.Kind.ToString());
        command.Parameters.AddWithValue("$textContent", (object?)item.TextContent ?? DBNull.Value);
        command.Parameters.AddWithValue("$callId", (object?)item.CallId ?? DBNull.Value);
        command.Parameters.AddWithValue("$toolId", (object?)item.ToolId ?? DBNull.Value);
        command.Parameters.AddWithValue("$argumentsJson", (object?)item.ArgumentsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$resultSummary", (object?)item.ResultSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("$structuredPayloadJson", (object?)item.StructuredPayloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourcesJson", (object?)item.SourcesJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$wasTruncated", item.WasTruncated ? 1 : 0);
        command.Parameters.AddWithValue("$isError", item.IsError ? 1 : 0);
        command.Parameters.AddWithValue("$errorCode", (object?)item.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$backendId", (object?)item.BackendId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

}
