using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tools.Web.Backends;

public sealed record WebSearchBackendResult(
    string Summary,
    string StructuredPayloadJson,
    IReadOnlyList<AgentToolSourceItem> Sources,
    bool WasTruncated,
    string BackendId,
    string? Content = null);
