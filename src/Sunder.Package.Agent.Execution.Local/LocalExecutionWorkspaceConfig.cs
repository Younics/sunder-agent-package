namespace Sunder.Package.Agent.Execution.Local;

public sealed record LocalExecutionWorkspaceConfig(
    string? SelectedShellId = null,
    IReadOnlyList<string>? PathEntries = null);

internal sealed record LocalExecutionRuntimeConfig(
    IReadOnlyList<string> WorkspacePaths,
    string? DefaultWorkingDirectory,
    string? SelectedShellId = null,
    IReadOnlyList<string>? PathEntries = null);
