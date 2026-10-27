using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    // Compare the storage/domain result with the generated wire result. This is a
    // scenario oracle only: no legacy dispatcher or second provider is executed.
    private static async Task<JsonElement> CatalogDomainResult(IRuntimeCatalogService catalog, string route, JsonElement request)
    {
        string Text(string name) => request.GetProperty(name).GetString()!;
        Guid Session() => request.GetProperty("sessionId").GetGuid();
        int Limit() => request.GetProperty("limit").GetInt32();
        DateTimeOffset Created() => request.GetProperty("createdAtUtc").GetDateTimeOffset();
        Guid Turn() => request.GetProperty("turnId").GetGuid();
        object? result = route switch
        {
            "list-sessions" => await catalog.ListSessionsAsync(),
            "list-sessions-for-profile" => await catalog.ListSessionsForProfileAsync(Text("value")),
            "list-sessions-for-workspace" => await catalog.ListSessionsForWorkspaceAsync(Text("value")),
            "get-session" => await catalog.GetSessionAsync(Session()),
            "list-workspaces" => await catalog.ListWorkspacesAsync(),
            "get-workspace" => await catalog.GetWorkspaceAsync(Text("value")),
            "get-session-profile" => await catalog.GetSessionProfileAsync(Session()),
            "get-working-summary" => await catalog.GetWorkingSummaryAsync(Session()),
            "get-session-context-checkpoint" => await catalog.GetSessionContextCheckpointAsync(Session()),
            "get-latest-checkpoint" => await catalog.GetLatestCheckpointAsync(Session()),
            "get-run-activity" => await catalog.GetRunActivityAsync(Session()),
            "list-recent-turns" => await catalog.ListRecentTurnsAsync(Session(), Limit()),
            "list-turns-before" => await catalog.ListTurnsBeforeAsync(Session(), Created(), Turn(), Limit()),
            "list-turns-after" => await catalog.ListTurnsAfterAsync(Session(), Created(), Turn(), Limit()),
            "list-recent-transcript-headers" => await catalog.ListRecentTranscriptHeadersAsync(Session(), Limit()),
            "list-transcript-headers-before" => await catalog.ListTranscriptHeadersBeforeAsync(Session(), Created(), Turn(), Limit()),
            "list-transcript-headers-after" => await catalog.ListTranscriptHeadersAfterAsync(Session(), Created(), Turn(), Limit()),
            "get-transcript-tool-detail" => await catalog.GetTranscriptToolDetailAsync(request.Deserialize<AgentTranscriptToolDetailRequest>(ExecutionJson)!),
            "read-compact-transcript" => await catalog.ReadCompactTranscriptAsync(request.Deserialize<AgentCompactTranscriptRequest>(CurrentPrivateRpcDescriptors.OwnerJson)!),
            "read-activity-members" => await catalog.ReadActivityMembersAsync(request.Deserialize<AgentTranscriptActivityMembersRequest>(CurrentPrivateRpcDescriptors.OwnerJson)!),
            "list-profiles" => await catalog.ListProfilesAsync(),
            "get-profile" => await catalog.GetProfileAsync(Text("value")),
            "get-session-model-binding" => await catalog.GetSessionModelBindingAsync(Session(), Text("capabilityKind")),
            "get-model-binding" => await catalog.GetModelBindingAsync(Text("profileId"), Text("capabilityKind")),
            _ => throw new InvalidOperationException("Uncovered catalog scenario: " + route),
        };
        return JsonSerializer.SerializeToElement(result, result?.GetType() ?? typeof(object), ExecutionJson);
    }
}
