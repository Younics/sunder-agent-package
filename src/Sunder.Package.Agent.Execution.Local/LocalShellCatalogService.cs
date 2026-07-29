using System.Text.Json;
using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Local;

public sealed class LocalShellCatalogService
{
    private const string CustomShellsKey = "shells.custom";
    private const int CurrentSchemaVersion = 1;
    private const int MaximumShellCount = 32;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly ConditionalWeakTable<IPackageKeyValueStore, SemaphoreSlim> MutationGates = new();
    private readonly IPackageContext packageContext;
    private readonly SemaphoreSlim _mutationGate;

    public LocalShellCatalogService(IPackageContext packageContext)
    {
        this.packageContext = packageContext;
        _mutationGate = MutationGates.GetValue(
            packageContext.Storage.State,
            static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<IReadOnlyList<LocalShellDefinition>> ListShellsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        var result = new List<LocalShellDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shells = snapshot.DetectedShells
            .Concat(snapshot.CustomShells.OrderBy(shell => shell.DisplayName, StringComparer.OrdinalIgnoreCase));

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
        => (await GetSnapshotAsync(cancellationToken)).CustomShells;

    internal async Task<LocalShellCatalogSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken);
        return new LocalShellCatalogSnapshot(
            state.Revision,
            DetectShells(),
            state.Shells);
    }

    internal async Task<LocalShellCatalogSnapshot> SaveCustomShellsAsync(
        IReadOnlyList<LocalShellDefinition> shells,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shells);
        if (shells.Count > MaximumShellCount)
        {
            throw new LocalExecutionDomainException(
                "local.shell-catalog.too-many",
                $"At most {MaximumShellCount} custom shells may be configured.");
        }

        var normalized = NormalizeCustomShells(shells);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadStateAsync(cancellationToken);
            if (current.Revision != expectedRevision)
            {
                throw new LocalExecutionDomainException(
                    "local.shell-catalog.conflict",
                    "Shell settings changed in Runtime. Refresh and retry without discarding your draft.",
                    isTransient: true);
            }

            var updated = new LocalShellCatalogState(
                CurrentSchemaVersion,
                checked(current.Revision + 1),
                normalized);
            await packageContext.Storage.State.SetValueAsync(
                CustomShellsKey,
                JsonSerializer.Serialize(updated, JsonOptions),
                cancellationToken);
            return new LocalShellCatalogSnapshot(
                updated.Revision,
                DetectShells(),
                updated.Shells);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<LocalShellCatalogState> LoadStateAsync(CancellationToken cancellationToken)
    {
        var json = await packageContext.Storage.State.GetValueAsync(CustomShellsKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new LocalShellCatalogState(CurrentSchemaVersion, 0, []);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var legacy = JsonSerializer.Deserialize<IReadOnlyList<LocalShellDefinition>>(json, JsonOptions)
                    ?? throw new JsonException("The legacy shell list is null.");
                return new LocalShellCatalogState(CurrentSchemaVersion, 0, NormalizeCustomShells(legacy));
            }

            var state = JsonSerializer.Deserialize<LocalShellCatalogState>(json, JsonOptions)
                ?? throw new JsonException("The shell catalog is null.");
            if (state.SchemaVersion != CurrentSchemaVersion || state.Revision < 0 || state.Shells is null)
            {
                throw new LocalExecutionDomainException(
                    "local.shell-catalog.unsupported-schema",
                    "The stored shell catalog uses an unsupported schema and was left unchanged.");
            }
            return state with { Shells = NormalizeCustomShells(state.Shells) };
        }
        catch (LocalExecutionDomainException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new LocalExecutionDomainException(
                "local.shell-catalog.malformed",
                "The stored shell catalog is malformed and was left unchanged.",
                innerException: exception);
        }
    }

    private static IReadOnlyList<LocalShellDefinition> NormalizeCustomShells(
        IEnumerable<LocalShellDefinition> shells)
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
            .GroupBy(shell => shell.ShellId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(shell => shell.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length > MaximumShellCount)
        {
            throw new LocalExecutionDomainException(
                "local.shell-catalog.too-many",
                $"At most {MaximumShellCount} custom shells may be configured.");
        }
        if (normalized.Any(shell => string.IsNullOrWhiteSpace(shell.ShellId)
                                    || shell.ShellId.Length > 128))
        {
            throw new LocalExecutionDomainException(
                "local.shell.invalid-id",
                "Custom shell identifiers must be non-empty and at most 128 characters.");
        }
        return normalized;
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

internal sealed record LocalShellCatalogSnapshot(
    long Revision,
    IReadOnlyList<LocalShellDefinition> DetectedShells,
    IReadOnlyList<LocalShellDefinition> CustomShells);

internal sealed record LocalShellCatalogState(
    int SchemaVersion,
    long Revision,
    IReadOnlyList<LocalShellDefinition> Shells);

internal sealed class LocalExecutionDomainException : InvalidOperationException
{
    internal LocalExecutionDomainException(
        string code,
        string message,
        bool isTransient = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        IsTransient = isTransient;
    }

    internal string Code { get; }

    internal bool IsTransient { get; }
}

public sealed record LocalShellDefinition(
    string ShellId,
    string DisplayName,
    string ExecutablePath,
    string SyntaxKind,
    bool IsDetected);
