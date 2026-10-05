using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    // Compare the storage/domain result with the generated wire result. This is a
    // scenario oracle only: no legacy dispatcher or second provider is executed.
    private static async Task<JsonElement> CatalogDomainResult(IAgentRuntimeCatalog catalog, string route, JsonElement request)
    {
        string Text(string name) => request.GetProperty(name).GetString()!;
        Guid Session() => request.GetProperty("sessionId").GetGuid();
        int Limit() => request.GetProperty("limit").GetInt32();
        DateTimeOffset Created() => request.GetProperty("createdAtUtc").GetDateTimeOffset();
        Guid Turn() => request.GetProperty("turnId").GetGuid();
        var transcript = (IAgentTranscriptCatalog)catalog;
        object? result = route switch
        {
            "list-sessions" => catalog.ListSessions(),
            "list-sessions-for-profile" => catalog.ListSessionsForProfile(Text("value")),
            "list-sessions-for-workspace" => catalog.ListSessionsForWorkspace(Text("value")),
            "get-session" => catalog.GetSession(Session()),
            "list-workspaces" => await catalog.ListWorkspacesAsync(),
            "get-workspace" => await catalog.GetWorkspaceAsync(Text("value")),
            "get-session-profile" => catalog.GetSessionProfile(Session()),
            "get-working-summary" => catalog.GetWorkingSummary(Session()),
            "get-session-context-checkpoint" => catalog.GetLatestSessionContextCheckpoint(Session()),
            "get-latest-checkpoint" => catalog.GetLatestCheckpoint(Session()),
            "list-recent-turns" => catalog.ListRecentTurns(Session(), Limit()),
            "list-turns-before" => catalog.ListTurnsBefore(Session(), Created(), Turn(), Limit()),
            "list-turns-after" => catalog.ListTurnsAfter(Session(), Created(), Turn(), Limit()),
            "list-recent-transcript-headers" => transcript.ListRecentTranscriptHeaders(Session(), Limit()),
            "list-transcript-headers-before" => transcript.ListTranscriptHeadersBefore(Session(), Created(), Turn(), Limit()),
            "list-transcript-headers-after" => transcript.ListTranscriptHeadersAfter(Session(), Created(), Turn(), Limit()),
            "get-transcript-tool-detail" => transcript.GetTranscriptToolDetail(request.Deserialize<AgentTranscriptToolDetailRequest>(ExecutionJson)!),
            "list-profiles" => catalog.ListProfiles(),
            "get-profile" => catalog.GetProfile(Text("value")),
            "get-session-model-binding" => catalog.GetSessionModelBinding(Session(), Text("capabilityKind")),
            "get-model-binding" => catalog.GetModelBinding(Text("profileId"), Text("capabilityKind")),
            _ => throw new InvalidOperationException("Uncovered catalog scenario: " + route),
        };
        return JsonSerializer.SerializeToElement(result, result?.GetType() ?? typeof(object), ExecutionJson);
    }
}
