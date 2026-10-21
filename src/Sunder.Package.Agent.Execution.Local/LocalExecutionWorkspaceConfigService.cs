using System.Collections.Concurrent;
using Sunder.Agent.Execution.Common;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Shared.Storage;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Local;

public sealed class LocalExecutionWorkspaceConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> SupportedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "SelectedShellId",
        "PathEntries",
    };
    private static readonly ConditionalWeakTable<IPackageKeyValueStore, ConcurrentDictionary<string, SemaphoreSlim>> MutationGates = new();
    private readonly IPackageContext _packageContext;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mutationGates;

    public LocalExecutionWorkspaceConfigService(IPackageContext packageContext)
    {
        _packageContext = packageContext;
        _mutationGates = MutationGates.GetValue(
            packageContext.Storage.State,
            static _ => new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal));
    }

    public async Task<LocalExecutionWorkspaceConfig> GetConfigAsync(
        string bindingId,
        CancellationToken cancellationToken = default)
    {
        var json = await _packageContext.Storage.State.GetValueAsync(BuildKey(bindingId), cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new LocalExecutionWorkspaceConfig(null, []);
        }

        try
        {
            using var document = ParseAndValidateDocument(json);
            var config = JsonSerializer.Deserialize<LocalExecutionWorkspaceConfig>(json, JsonOptions)
                ?? throw new JsonException("Local workspace configuration is null.");
            return Normalize(config);
        }
        catch (LocalExecutionDomainException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
                or NotSupportedException
                or ArgumentException
                or PathTooLongException)
        {
            throw new LocalExecutionDomainException(
                "local.workspace-config.malformed",
                "The stored Local workspace configuration is malformed and was left unchanged.",
                innerException: exception);
        }
    }

    public async Task SaveConfigAsync(
        string bindingId,
        LocalExecutionWorkspaceConfig config,
        CancellationToken cancellationToken = default)
    {
        var gate = _mutationGates.GetOrAdd(bindingId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveConfigCoreAsync(bindingId, config, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task UpdateConfigAsync(
        string bindingId,
        Func<LocalExecutionWorkspaceConfig, CancellationToken, ValueTask<LocalExecutionWorkspaceConfig>> update,
        CancellationToken cancellationToken)
    {
        var gate = _mutationGates.GetOrAdd(bindingId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await GetConfigAsync(bindingId, cancellationToken).ConfigureAwait(false);
            var updated = await update(current, cancellationToken).ConfigureAwait(false);
            await SaveConfigCoreAsync(bindingId, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private Task SaveConfigCoreAsync(
        string bindingId,
        LocalExecutionWorkspaceConfig config,
        CancellationToken cancellationToken)
        => _packageContext.Storage.State.SetValueAsync(
            BuildKey(bindingId),
            JsonSerializer.Serialize(Normalize(config), JsonOptions),
            cancellationToken);

    internal static string CreateGeneration(LocalExecutionConfigurationSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(
            snapshot with { WorkspaceConfig = Normalize(snapshot.WorkspaceConfig) },
            JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("local-execution-config-v1\n" + json)))
            .ToLowerInvariant();
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
        => CurrentPackageStorageKey.Create("workspace-bindings.config", 2, bindingId);

    private static JsonDocument ParseAndValidateDocument(string json)
    {
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Local workspace configuration is not an object.");
            }

            var properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!properties.Add(property.Name))
                {
                    throw new JsonException(
                        $"Local workspace configuration contains duplicate or case-colliding property '{property.Name}'.");
                }
                if (!SupportedProperties.Contains(property.Name))
                {
                    throw new LocalExecutionDomainException(
                        "local.workspace-config.unsupported-schema",
                        "The stored Local workspace configuration contains unsupported data and was left unchanged.");
                }
            }

            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

}
