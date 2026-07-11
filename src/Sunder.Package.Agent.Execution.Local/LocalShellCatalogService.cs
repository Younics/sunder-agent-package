using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Local;

public sealed class LocalShellCatalogService(IPackageContext packageContext)
{
    private const string CustomShellsKey = "shells.custom";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<LocalShellDefinition>> ListShellsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<LocalShellDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shells = DetectShells()
            .Concat((await ListCustomShellsAsync(cancellationToken)).OrderBy(shell => shell.DisplayName, StringComparer.OrdinalIgnoreCase));

        foreach (var shell in shells)
        {
            if (seen.Add(shell.ShellId))
            {
                result.Add(shell);
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<LocalShellDefinition>> ListCustomShellsAsync(CancellationToken cancellationToken = default)
    {
        var json = await packageContext.Storage.State.GetValueAsync(CustomShellsKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<LocalShellDefinition>>(json, JsonOptions)
                       ?.Where(shell => !shell.IsDetected)
                       .ToArray()
                   ?? [];
        }
        catch
        {
            return [];
        }
    }

    public Task SaveCustomShellsAsync(
        IReadOnlyList<LocalShellDefinition> shells,
        CancellationToken cancellationToken = default)
    {
        var normalized = shells
            .Where(shell => !shell.IsDetected && !string.IsNullOrWhiteSpace(shell.ExecutablePath))
            .Select(shell => shell with
            {
                ShellId = string.IsNullOrWhiteSpace(shell.ShellId) ? "custom-" + Guid.NewGuid().ToString("N") : shell.ShellId,
                DisplayName = string.IsNullOrWhiteSpace(shell.DisplayName) ? Path.GetFileNameWithoutExtension(shell.ExecutablePath) : shell.DisplayName.Trim(),
                ExecutablePath = shell.ExecutablePath.Trim(),
                SyntaxKind = NormalizeSyntaxKind(shell.SyntaxKind),
                IsDetected = false,
            })
            .ToArray();
        return packageContext.Storage.State.SetValueAsync(
            CustomShellsKey,
            JsonSerializer.Serialize(normalized, JsonOptions),
            cancellationToken);
    }

    public async Task<LocalShellDefinition> GetDefaultShellAsync(CancellationToken cancellationToken = default)
        => (await ListShellsAsync(cancellationToken)).FirstOrDefault()
           ?? new LocalShellDefinition("cmd", "Command Prompt", Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", AgentShellSyntaxKinds.Cmd, true);

    public async Task<LocalShellDefinition> ResolveShellAsync(string? shellId, CancellationToken cancellationToken = default)
    {
        var shells = await ListShellsAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(shellId)
            ? shells.FirstOrDefault() ?? await GetDefaultShellAsync(cancellationToken)
            : shells.FirstOrDefault(shell => string.Equals(shell.ShellId, shellId, StringComparison.OrdinalIgnoreCase))
                ?? await GetDefaultShellAsync(cancellationToken);
    }

    private static IReadOnlyList<LocalShellDefinition> DetectShells()
    {
        var shells = new List<LocalShellDefinition>();
        if (OperatingSystem.IsWindows())
        {
            AddIfFound(shells, "pwsh", "PowerShell 7", "pwsh.exe", AgentShellSyntaxKinds.PowerShell);
            AddIfFound(shells, "powershell", "Windows PowerShell", "powershell.exe", AgentShellSyntaxKinds.PowerShell);
            shells.Add(new LocalShellDefinition("cmd", "Command Prompt", Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", AgentShellSyntaxKinds.Cmd, true));
            return shells;
        }

        AddIfExists(shells, "sh", "POSIX sh", "/bin/sh", AgentShellSyntaxKinds.PosixSh);
        AddIfExists(shells, "bash", "Bash", "/bin/bash", AgentShellSyntaxKinds.PosixSh);
        AddIfExists(shells, "zsh", "Zsh", "/bin/zsh", AgentShellSyntaxKinds.PosixSh);
        return shells;
    }

    private static void AddIfFound(List<LocalShellDefinition> shells, string shellId, string displayName, string executableName, string syntaxKind)
    {
        var path = ResolveExecutableOnPath(executableName);
        if (path is not null)
        {
            shells.Add(new LocalShellDefinition(shellId, displayName, path, syntaxKind, true));
        }
    }

    private static void AddIfExists(List<LocalShellDefinition> shells, string shellId, string displayName, string path, string syntaxKind)
    {
        if (File.Exists(path))
        {
            shells.Add(new LocalShellDefinition(shellId, displayName, path, syntaxKind, true));
        }
    }

    private static string? ResolveExecutableOnPath(string executableName)
    {
        if (Path.IsPathRooted(executableName) && File.Exists(executableName))
        {
            return executableName;
        }

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var path in paths)
        {
            var candidate = Path.Combine(path, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static string NormalizeSyntaxKind(string? syntaxKind)
        => syntaxKind switch
        {
            AgentShellSyntaxKinds.PowerShell => AgentShellSyntaxKinds.PowerShell,
            AgentShellSyntaxKinds.Cmd => AgentShellSyntaxKinds.Cmd,
            AgentShellSyntaxKinds.PosixSh => AgentShellSyntaxKinds.PosixSh,
            _ => AgentShellSyntaxKinds.Custom,
        };
}

public sealed record LocalShellDefinition(
    string ShellId,
    string DisplayName,
    string ExecutablePath,
    string SyntaxKind,
    bool IsDetected);
