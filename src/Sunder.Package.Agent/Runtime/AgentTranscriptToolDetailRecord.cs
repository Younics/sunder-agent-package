namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Identifies one authoritative tool invocation detail lookup.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentTranscriptToolDetailRequest(
    Guid SessionId,
    Guid? ToolExecutionId = null,
    string? CallId = null,
    Guid? ItemId = null,
    Guid? RunId = null,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] long? RunRevision = null);

/// <summary>Contains the authoritative persisted fields used to present one tool invocation.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentTranscriptToolDetailRecord(
    Guid SessionId,
    Guid? ToolExecutionId,
    string? CallId,
    Guid? CallItemId,
    Guid? ResultItemId,
    string ToolId,
    string? ArgumentsJson,
    string? OutputText,
    string? ResultSummary,
    string? StructuredPayloadJson,
    string? SourcesJson,
    string? PresentationPayloadJson,
    bool WasTruncated,
    bool IsError,
    string? ErrorCode,
    string? BackendId,
    AgentToolExecutionStatus? Status,
    string? ToolOwnerPackageId,
    string? ToolSchemaId,
    string? ToolSchemaVersion,
    [property: Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")] long Revision)
{
    /// <summary>Gets whether transport limits required one or more detail fields to be omitted or shortened.</summary>
    public bool WasTransportTruncated { get; init; }

    /// <summary>Gets the durable run that owns the invocation, when available.</summary>
    public Guid? RunId { get; init; }

    /// <summary>Gets the owning run revision used to scope the provider call identifier.</summary>
    [Sunder.Sdk.Services.SunderValueConstraints(Minimum = "0")]
    public long? RunRevision { get; init; }
}
