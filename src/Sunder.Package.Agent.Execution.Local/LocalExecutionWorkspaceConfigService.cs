using Sunder.Agent.Execution.Common;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Storage;

namespace Sunder.Package.Agent.Execution.Local;

public sealed class LocalExecutionWorkspaceConfigService : IAgentWorkspacePathMigrationContributor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IPackageContext _packageContext;
    private readonly LocalPackageStorageMigration _storageMigration;

    public LocalExecutionWorkspaceConfigService(IPackageContext packageContext)
        : this(packageContext, new LocalPackageStorageMigration(packageContext))
    {
    }

    internal LocalExecutionWorkspaceConfigService(
        IPackageContext packageContext,
        LocalPackageStorageMigration storageMigration)
    {
        _packageContext = packageContext;
        _storageMigration = storageMigration;
    }

    public string ContributorId => "sunder.package.agent.execution.local.workspace-path-migration";

    public async Task<LocalExecutionWorkspaceConfig> GetConfigAsync(
        string bindingId,
        CancellationToken cancellationToken = default)
    {
        await _storageMigration.EnsureAsync(cancellationToken).ConfigureAwait(false);
        var json = await _packageContext.Storage.State.GetValueAsync(BuildKey(bindingId), cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new LocalExecutionWorkspaceConfig(null, []);
        }

        try
        {
            var config = JsonSerializer.Deserialize<LocalExecutionWorkspaceConfig>(json, JsonOptions);
            return Normalize(config ?? new LocalExecutionWorkspaceConfig(null, []));
        }
        catch
        {
            return new LocalExecutionWorkspaceConfig(null, []);
        }
    }

    public async Task SaveConfigAsync(
        string bindingId,
        LocalExecutionWorkspaceConfig config,
        CancellationToken cancellationToken = default)
    {
        await _storageMigration.EnsureAsync(cancellationToken).ConfigureAwait(false);
        var normalized = Normalize(config);
        await _packageContext.Storage.State.SetValueAsync(
            BuildKey(bindingId),
            JsonSerializer.Serialize(normalized, JsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    private static LocalExecutionWorkspaceConfig Normalize(LocalExecutionWorkspaceConfig config)
    {
        var pathEntries = (config.PathEntries ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(ExpandPath(path.Trim())))
            .Distinct(PathStringComparer)
            .ToArray();

        return new LocalExecutionWorkspaceConfig(
            string.IsNullOrWhiteSpace(config.SelectedShellId) ? null : config.SelectedShellId.Trim(),
            pathEntries);
    }

    public bool CanMigrate(AgentWorkspacePathMigrationContext context)
        => string.Equals(context.Binding.ContributionId, "local", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<AgentWorkspacePathMigrationItem>> GetLegacyWorkspacePathsAsync(
        AgentWorkspacePathMigrationContext context,
        CancellationToken cancellationToken = default)
    {
        await _storageMigration.EnsureAsync(cancellationToken).ConfigureAwait(false);
        var json = await _packageContext.Storage.State.GetValueAsync(BuildKey(context.Binding.BindingId), cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("AllowedRoots", out var rootsElement) || rootsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var defaultWorkingDirectory = TryGetString(root, "DefaultWorkingDirectory");
            var normalizedDefault = string.IsNullOrWhiteSpace(defaultWorkingDirectory)
                ? null
                : Path.GetFullPath(ExpandPath(defaultWorkingDirectory.Trim()));
            var items = new List<AgentWorkspacePathMigrationItem>();
            var index = 0;
            foreach (var item in rootsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = item.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var hostPath = Path.GetFullPath(ExpandPath(value.Trim()));
                var isDefault = normalizedDefault is not null && IsSameOrChildPath(normalizedDefault, hostPath);
                items.Add(new AgentWorkspacePathMigrationItem(hostPath, isDefault, SortOrder: index++));
            }

            return items;
        }
        catch
        {
            return [];
        }
    }

    public async Task CompleteWorkspacePathMigrationAsync(
        AgentWorkspacePathMigrationContext context,
        CancellationToken cancellationToken = default)
        => await SaveConfigAsync(
            context.Binding.BindingId,
            await GetConfigAsync(context.Binding.BindingId, cancellationToken),
            cancellationToken);

    internal static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : Environment.ExpandEnvironmentVariables(path);
    }

    internal static bool IsSameOrChildPath(string candidatePath, string rootPath)
        => HostPath.IsSameOrChild(candidatePath, rootPath, caseInsensitive: OperatingSystem.IsWindows());

    internal static StringComparer PathStringComparer
        => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    internal static string BuildKey(string bindingId)
        => PackageStorageKeyFactory.Create("workspace-bindings.config", 2, bindingId);

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
