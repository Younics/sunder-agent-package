namespace Sunder.Package.Agent.Mcp.Services;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record McpCatalogDiagnostic(string StorageKey, string Message);
