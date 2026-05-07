namespace Sunder.Package.Agent.Execution.Local;

public sealed record LocalExecutionWorkspaceConfig(
    IReadOnlyList<string> AllowedRoots,
    string? DefaultWorkingDirectory,
    string? SelectedShellId = null);
