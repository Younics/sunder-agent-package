using Sunder.Package.Agent.Mcp.Services;

namespace Sunder.Package.Agent.Mcp.Runtime;

internal interface IMcpManagementGateway
{
    event Action? ServersChanged;
    event Action? StatusChanged;
    IReadOnlyList<McpCatalogDiagnostic> Diagnostics { get; }
    Task<IReadOnlyList<ConfiguredMcpServerRecord>> ListAsync(CancellationToken cancellationToken = default);
    Task<string?> LoadDocumentAsync(string serverId, CancellationToken cancellationToken = default);
    string NormalizeName(string? name);
    ParsedMcpServerConfiguration Parse(string serverId, string name, string document, ConfiguredMcpServerRecord? existing);
    string Format(string serverId, string name, string document, ConfiguredMcpServerRecord? existing);
    Task<string> SaveAsync(ParsedMcpServerConfiguration parsed, CancellationToken cancellationToken = default);
    Task DeleteAsync(string serverId, CancellationToken cancellationToken = default);
    Task<McpConfigurationImportResult> InitializeAsync(CancellationToken cancellationToken = default);
    Task<McpConfigurationImportResult> ImportCommonAsync(CancellationToken cancellationToken = default);
    Task<McpConfigurationImportResult> ImportFileAsync(string filePath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> DiscoverAsync(ConfiguredMcpServerRecord server, bool reconnect, CancellationToken cancellationToken);
    Task DisconnectAsync(string serverId, CancellationToken cancellationToken = default);
    Task<McpConnectionPresentation> GetPresentationAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken = default);
    Task AuthorizeAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken);
    Task ClearAuthorizationAsync(ConfiguredMcpServerRecord server, CancellationToken cancellationToken);
}

internal interface IMcpOwnerManagementClient
{
    ValueTask<McpProjection> QueryAsync(McpQuery query, CancellationToken cancellationToken = default);
    ValueTask<McpProjection> CommandAsync(McpCommand command, CancellationToken cancellationToken = default);
    IAsyncEnumerable<McpChanged> WatchChangesAsync(CancellationToken cancellationToken = default);
}

internal enum McpQueryKind
{
    Catalog,
    Document,
    Presentation,
}

internal sealed record McpQuery(McpQueryKind Kind, string? ServerId = null);

internal enum McpCommandKind
{
    Initialize,
    Save,
    Delete,
    ImportCommon,
    ImportDocument,
    Discover,
    Reconnect,
    Disconnect,
    ClearAuthorization,
}

internal sealed record McpCommand(
    McpCommandKind Kind,
    string? ServerId = null,
    ParsedMcpServerConfiguration? Configuration = null,
    string? Document = null,
    string? FileName = null);

internal sealed record McpProjection(
    IReadOnlyList<ConfiguredMcpServerRecord>? Servers = null,
    IReadOnlyList<McpCatalogDiagnostic>? Diagnostics = null,
    string? Document = null,
    McpConnectionPresentation? Presentation = null,
    McpConfigurationImportResult? ImportResult = null,
    IReadOnlyList<string>? ToolNames = null);

internal enum McpChangeKind
{
    Servers,
    Status,
}

internal sealed record McpChanged(McpChangeKind Kind);
