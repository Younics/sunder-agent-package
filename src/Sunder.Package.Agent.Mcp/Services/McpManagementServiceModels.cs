namespace Sunder.Package.Agent.Mcp.Services;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record McpConnectionPresentation(
    McpConnectionStatus Status,
    string Detail,
    string Diagnostics);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record McpConfigurationImportResult(
    int ImportedCount,
    int SkippedCount,
    IReadOnlyList<string> Warnings);

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed class McpManagementOperationException(
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException);
