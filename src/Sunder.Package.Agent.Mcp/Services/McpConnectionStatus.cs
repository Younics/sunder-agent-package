namespace Sunder.Package.Agent.Mcp.Services;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum McpConnectionStatusKind
{
    Disabled = 0,
    Idle,
    Connecting,
    DiscoveringTools,
    Connected,
    Error,
    Disconnected,
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record McpConnectionStatus(
    string ServerId,
    McpConnectionStatusKind Kind,
    string Message,
    int ActiveConnectionCount,
    int? ToolCount = null,
    IReadOnlyList<string>? ToolNames = null,
    DateTimeOffset? LastChangedAtUtc = null,
    string? Error = null,
    IReadOnlyList<string>? StandardErrorTail = null);
