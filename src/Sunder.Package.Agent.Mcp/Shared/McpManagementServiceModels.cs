namespace Sunder.Package.Agent.Mcp.Services;

public sealed record McpConnectionPresentation(
    McpConnectionStatus Status,
    string Detail,
    string Diagnostics);

public sealed record McpConfigurationImportResult(
    int ImportedCount,
    int SkippedCount,
    IReadOnlyList<string> Warnings);

internal sealed class McpManagementOperationException(
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException);
