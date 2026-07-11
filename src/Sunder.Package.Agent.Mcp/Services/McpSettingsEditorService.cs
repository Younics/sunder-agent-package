namespace Sunder.Package.Agent.Mcp.Services;

public sealed class McpSettingsEditorService(McpServerCatalogService catalog)
{
    internal IReadOnlyList<McpCatalogDiagnostic> Diagnostics => catalog.LastDiagnostics;

    internal event Action? ServersChanged
    {
        add => catalog.ServersChanged += value;
        remove => catalog.ServersChanged -= value;
    }

    internal Task<IReadOnlyList<ConfiguredMcpServerRecord>> ListAsync(CancellationToken cancellationToken = default)
        => catalog.ListServersAsync(cancellationToken);

    internal Task<string?> LoadDocumentAsync(string serverId, CancellationToken cancellationToken = default)
        => catalog.ExportServerJsonAsync(serverId, cancellationToken);

    internal string NormalizeName(string? name) => catalog.NormalizeServerName(name);

    internal ParsedMcpServerConfiguration Parse(
        string serverId,
        string name,
        string document,
        ConfiguredMcpServerRecord? existing)
        => McpConfigurationDocument.Parse(serverId, NormalizeName(name), document, existing);

    internal string Format(string serverId, string name, string document, ConfiguredMcpServerRecord? existing)
    {
        var parsed = Parse(serverId, name, document, existing);
        return McpConfigurationDocument.BuildEditorText(parsed.Server, parsed.Headers, parsed.EnvironmentVariables);
    }

    internal Task SaveAsync(ParsedMcpServerConfiguration parsed, CancellationToken cancellationToken = default)
        => catalog.SaveServerAsync(parsed.Server, parsed.Headers, parsed.EnvironmentVariables, cancellationToken);

    internal Task DeleteAsync(string serverId, CancellationToken cancellationToken = default)
        => catalog.DeleteServerAsync(serverId, cancellationToken);
}
