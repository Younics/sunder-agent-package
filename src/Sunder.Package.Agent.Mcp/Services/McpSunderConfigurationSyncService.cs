using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Mcp.Services;

public sealed class McpSunderConfigurationSyncService(
    McpEcosystemConfigurationImporter importer,
    AgentRpcCatalog? rpcCatalog = null,
    string? userProfilePath = null,
    string? applicationDataPath = null)
{
    private readonly McpEcosystemConfigurationImporter _importer = importer;
    private readonly AgentRpcCatalog? _rpcCatalog = rpcCatalog;
    private readonly string? _userProfilePath = userProfilePath;
    private readonly string? _applicationDataPath = applicationDataPath;
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    public Task<McpConfigurationImportResult> SyncAsync(CancellationToken cancellationToken = default)
        => SyncCoreAsync(workspace: null, includeKnownWorkspaces: true, cancellationToken);

    public Task<McpConfigurationImportResult> SyncWorkspaceAsync(
        AgentWorkspaceRecord? workspace,
        CancellationToken cancellationToken = default)
        => SyncCoreAsync(workspace, includeKnownWorkspaces: workspace is null, cancellationToken);

    public async Task<McpConfigurationImportResult> SyncFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new MutableSyncResult();
            foreach (var path in filePaths.Where(File.Exists).Distinct(GetPathStringComparer()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    result.Add(await _importer.ImportSunderConfigurationFileAsync(path, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Skipped++;
                    result.Warnings.Add($"Skipped Sunder MCP config '{Path.GetFileName(path)}': {ex.Message}");
                }
            }

            return result.ToResult();
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private Task<McpConfigurationImportResult> SyncCoreAsync(
        AgentWorkspaceRecord? workspace,
        bool includeKnownWorkspaces,
        CancellationToken cancellationToken)
        => SyncFilesAsync(EnumerateConfigurationPaths(workspace, includeKnownWorkspaces), cancellationToken);

    private IEnumerable<string> EnumerateConfigurationPaths(AgentWorkspaceRecord? workspace, bool includeKnownWorkspaces)
    {
        foreach (var path in EnumerateGlobalConfigurationPaths())
        {
            yield return path;
        }

        if (workspace is not null)
        {
            foreach (var path in EnumerateWorkspaceConfigurationPaths(workspace))
            {
                yield return path;
            }
        }

        if (!includeKnownWorkspaces)
        {
            yield break;
        }

        foreach (var knownWorkspace in ListKnownWorkspaces())
        {
            foreach (var path in EnumerateWorkspaceConfigurationPaths(knownWorkspace))
            {
                yield return path;
            }
        }
    }

    private IEnumerable<string> EnumerateGlobalConfigurationPaths()
    {
        var home = string.IsNullOrWhiteSpace(_userProfilePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : _userProfilePath;
        if (!string.IsNullOrWhiteSpace(home))
        {
            var sunderConfigRoot = Path.Combine(home, ".config", "sunder");
            yield return Path.Combine(sunderConfigRoot, "mcp.json");
            foreach (var path in EnumerateJsonFiles(Path.Combine(sunderConfigRoot, "mcp")))
            {
                yield return path;
            }
        }

        if (!OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(_applicationDataPath))
        {
            yield break;
        }

        var appData = string.IsNullOrWhiteSpace(_applicationDataPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : _applicationDataPath;
        if (string.IsNullOrWhiteSpace(appData))
        {
            yield break;
        }

        var windowsSunderRoot = Path.Combine(appData, "Sunder");
        yield return Path.Combine(windowsSunderRoot, "mcp.json");
        foreach (var path in EnumerateJsonFiles(Path.Combine(windowsSunderRoot, "mcp")))
        {
            yield return path;
        }
    }

    private IReadOnlyList<AgentWorkspaceRecord> ListKnownWorkspaces()
    {
        if (_rpcCatalog is null)
        {
            return [];
        }

        var result = new List<AgentWorkspaceRecord>();
        foreach (var reference in _rpcCatalog.GetServiceReferences(AgentRpcServices.RuntimeCatalogs))
        {
            if (!reference.TryAcquire(out var lease))
            {
                continue;
            }
            IReadOnlyList<AgentWorkspaceRecord> workspaces;
            using (lease)
                try
                {
                    workspaces = lease.Service.ListWorkspaces().ToArray();
                    if (lease.RetirementToken.IsCancellationRequested)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

            result.AddRange(workspaces);
        }

        return result;
    }

    private static IEnumerable<string> EnumerateWorkspaceConfigurationPaths(AgentWorkspaceRecord workspace)
    {
        foreach (var workspacePath in workspace.Paths.OrderBy(path => path.SortOrder))
        {
            if (string.IsNullOrWhiteSpace(workspacePath.HostPath))
            {
                continue;
            }

            var hostPath = TryGetFullPath(ExpandPath(workspacePath.HostPath.Trim()));
            if (hostPath is not null)
            {
                yield return Path.Combine(hostPath, ".sunder", "mcp.json");
            }
        }
    }

    private static IEnumerable<string> EnumerateJsonFiles(string folderPath)
    {
        try
        {
            return Directory.Exists(folderPath)
                ? Directory.EnumerateFiles(folderPath, "*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, GetPathStringComparer()).ToArray()
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static string ExpandPath(string path)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : Environment.ExpandEnvironmentVariables(path);
    }

    private static StringComparer GetPathStringComparer()
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed class MutableSyncResult
    {
        public int Imported { get; set; }

        public int Skipped { get; set; }

        public List<string> Warnings { get; } = [];

        public void Add(McpConfigurationImportResult result)
        {
            Imported += result.ImportedCount;
            Skipped += result.SkippedCount;
            Warnings.AddRange(result.Warnings);
        }

        public McpConfigurationImportResult ToResult() => new(Imported, Skipped, Warnings.ToArray());
    }
}
