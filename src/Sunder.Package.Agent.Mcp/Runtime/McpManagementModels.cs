using Sunder.Package.Agent.Mcp.Services;

namespace Sunder.Package.Agent.Mcp.Runtime;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
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

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal interface IMcpOwnerManagementClient
{
    ValueTask<McpProjection> QueryAsync(McpQuery query, CancellationToken cancellationToken = default);
    ValueTask<McpProjection> CommandAsync(McpCommand command, CancellationToken cancellationToken = default);
    IAsyncEnumerable<McpChanged> WatchChangesAsync(CancellationToken cancellationToken = default);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum McpQueryKind
{
    Catalog,
    Document,
    Presentation,
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record McpQuery(McpQueryKind Kind, string? ServerId = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
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

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record McpCommand(
    McpCommandKind Kind,
    string? ServerId = null,
    ParsedMcpServerConfiguration? Configuration = null,
    string? Document = null,
    string? FileName = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record McpProjection(
    IReadOnlyList<ConfiguredMcpServerRecord>? Servers = null,
    IReadOnlyList<McpCatalogDiagnostic>? Diagnostics = null,
    string? Document = null,
    McpConnectionPresentation? Presentation = null,
    McpConfigurationImportResult? ImportResult = null,
    IReadOnlyList<string>? ToolNames = null);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum McpChangeKind
{
    Servers,
    Status,
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed record McpChanged(McpChangeKind Kind);
