using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    private static AgentTurnRecord CreateTextTurn(
        Guid turnId,
        Guid sessionId,
        AgentMessageRole role,
        AgentTurnKind kind,
        string content,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        bool isStreaming = false)
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
            updatedAtUtc)
        {
            IsStreaming = isStreaming,
        };

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
        DateTimeOffset updatedAtUtc,
        Guid? toolExecutionId = null,
        AgentToolExecutionStatus? toolExecutionStatus = null,
        string? toolOwnerPackageId = null,
        string? toolSchemaId = null,
        string? toolSchemaVersion = null)
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
                {
                    ToolExecutionId = toolExecutionId,
                    ToolExecutionStatus = toolExecutionStatus,
                    ToolOwnerPackageId = toolOwnerPackageId,
                    ToolSchemaId = toolSchemaId,
                    ToolSchemaVersion = toolSchemaVersion,
                }
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
        string? presentationPayloadJson,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        Guid? toolExecutionId = null,
        AgentToolExecutionStatus? toolExecutionStatus = null,
        string? toolOwnerPackageId = null,
        string? toolSchemaId = null,
        string? toolSchemaVersion = null)
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
                    backendId,
                    presentationPayloadJson)
                {
                    ToolExecutionId = toolExecutionId,
                    ToolExecutionStatus = toolExecutionStatus,
                    ToolOwnerPackageId = toolOwnerPackageId,
                    ToolSchemaId = toolSchemaId,
                    ToolSchemaVersion = toolSchemaVersion,
                }
            ],
            createdAtUtc,
            updatedAtUtc);
}
