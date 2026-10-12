using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Sunder.Sdk.Services;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Mcp.Services;

public sealed partial class McpSunderConfigurationSyncService(
    McpEcosystemConfigurationImporter importer,
    IServiceProviders<IRuntimeCatalogService> workspaceCatalog,
    string? userProfilePath = null,
    string? applicationDataPath = null)
{
    private readonly McpEcosystemConfigurationImporter _importer = importer;
    private readonly IServiceProviders<IRuntimeCatalogService> _workspaceCatalog = workspaceCatalog;
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

    private async Task<McpConfigurationImportResult> SyncCoreAsync(
        AgentWorkspaceRecord? workspace,
        bool includeKnownWorkspaces,
        CancellationToken cancellationToken)
    {
        var paths = await EnumerateConfigurationPathsAsync(
            workspace,
            includeKnownWorkspaces,
            cancellationToken).ConfigureAwait(false);
        return await SyncFilesAsync(paths, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> EnumerateConfigurationPathsAsync(
        AgentWorkspaceRecord? workspace,
        bool includeKnownWorkspaces,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        foreach (var path in EnumerateGlobalConfigurationPaths())
        {
            paths.Add(path);
        }

        if (workspace is not null)
        {
            foreach (var path in EnumerateWorkspaceConfigurationPaths(workspace))
            {
                paths.Add(path);
            }
        }

        if (!includeKnownWorkspaces)
        {
            return paths;
        }

        foreach (var knownWorkspace in await ListKnownWorkspacesAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var path in EnumerateWorkspaceConfigurationPaths(knownWorkspace))
            {
                paths.Add(path);
            }
        }

        return paths;
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

    private async Task<IReadOnlyList<AgentWorkspaceRecord>> ListKnownWorkspacesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var catalog = await _workspaceCatalog.ListAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<AgentWorkspaceRecord>();
            foreach (var provider in catalog.Providers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (provider.State != ServiceProviderState.Active || !await IsCurrentAsync(provider, cancellationToken).ConfigureAwait(false)) continue;
                IReadOnlyList<AgentWorkspaceRecord> workspaces;
                using var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, provider.RetirementToken);
                try { workspaces = await provider.Service.ListWorkspacesAsync(invocation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch
                {
                    // Optional-provider failure does not discard the other workspace catalogs.
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (await IsCurrentAsync(provider, cancellationToken).ConfigureAwait(false)) result.AddRange(workspaces);
            }
            return result;
        }
        catch (SunderRpcException error) when (!cancellationToken.IsCancellationRequested && error.Error.Kind == SunderRpcErrorKind.Unavailable)
        { return []; }
    }

    private static async ValueTask<bool> IsCurrentAsync(ServiceProvider<IRuntimeCatalogService> provider, CancellationToken token)
    {
        try { await provider.EnsureCurrentAsync(token).ConfigureAwait(false); return true; }
        catch (SunderRpcException error) when (error.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        { token.ThrowIfCancellationRequested(); return false; }
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
