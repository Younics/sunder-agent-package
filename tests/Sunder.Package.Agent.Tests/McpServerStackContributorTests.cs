using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Mcp;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class McpServerStackContributorTests
{
    [Fact]
    public async Task ExportAsync_OmitsHeaderAndEnvironmentSecretValues()
    {
        var root = CreateTempDirectory();
        try
        {
            var context = new TestPackageContext(root);
            var catalog = new McpServerCatalogService(context);
            var contributor = new McpServerStackContributor(catalog, context);
            var parsed = McpConfigurationDocument.Parse(
                "server-1",
                "github",
                """
                {
                  "type": "local",
                  "enabled": true,
                  "command": ["npx", "-y", "@modelcontextprotocol/server-github"],
                  "env": {
                    "GITHUB_TOKEN": "super-secret-token"
                  },
                  "description": "Private setup note"
                }
                """);
            await catalog.SaveServerAsync(parsed.Server, parsed.Headers, parsed.EnvironmentVariables);

            var contribution = await contributor.ExportAsync(new StackExportRequest(["server-1"]));

            var fragment = Assert.Single(contribution.Fragments);
            Assert.DoesNotContain("super-secret-token", fragment.JsonPayload, StringComparison.Ordinal);
            var requiredInput = Assert.Single(fragment.RequiredInputs ?? []);
            Assert.Equal("GITHUB_TOKEN", ReadFirstSecretName(fragment.JsonPayload, "environmentVariables"));
            Assert.Contains(contribution.Warnings, warning => warning.Contains("executable command", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ImportAsync_WithSecretInput_CreatesEnabledServerAndStoresSecret()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceContext = new TestPackageContext(Path.Combine(root, "source"));
            var sourceCatalog = new McpServerCatalogService(sourceContext);
            var sourceContributor = new McpServerStackContributor(sourceCatalog, sourceContext);
            var parsed = McpConfigurationDocument.Parse(
                "server-1",
                "linear",
                """
                {
                  "type": "remote",
                  "enabled": true,
                  "url": "https://mcp.linear.app/sse",
                  "headers": {
                    "Authorization": "Bearer original-token"
                  }
                }
                """);
            await sourceCatalog.SaveServerAsync(parsed.Server, parsed.Headers, parsed.EnvironmentVariables);
            var fragment = Assert.Single((await sourceContributor.ExportAsync(new StackExportRequest(["server-1"]))).Fragments);

            var targetContext = new TestPackageContext(Path.Combine(root, "target"));
            var targetCatalog = new McpServerCatalogService(targetContext);
            var targetContributor = new McpServerStackContributor(targetCatalog, targetContext);
            var importFragment = ToImportFragment(fragment);
            var preview = await targetContributor.PreviewImportAsync(new StackImportPreviewRequest(
                [importFragment],
                new Dictionary<string, string>(),
                new Dictionary<string, string>()));
            var action = Assert.Single(preview.Actions);
            var input = Assert.Single(preview.RequiredInputs);

            var result = await targetContributor.ImportAsync(new StackImportRequest(
                [importFragment],
                new Dictionary<string, string> { [input.InputId] = "Bearer imported-token" },
                new Dictionary<string, string>(),
                [action.ActionId]));

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            var importedServer = await targetCatalog.GetServerAsync("server-1");
            Assert.NotNull(importedServer);
            Assert.True(importedServer.IsEnabled);
            var editorText = await targetCatalog.ExportServerJsonAsync("server-1");
            Assert.NotNull(editorText);
            Assert.Contains("Bearer imported-token", editorText, StringComparison.Ordinal);
            Assert.DoesNotContain("original-token", editorText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ExportAsync_SplitsPublicAndSecretHeaders()
    {
        var root = CreateTempDirectory();
        try
        {
            var context = new TestPackageContext(root);
            var catalog = new McpServerCatalogService(context);
            var contributor = new McpServerStackContributor(catalog, context);
            var parsed = McpConfigurationDocument.Parse(
                "server-1",
                "stitch",
                """
                {
                  "type": "remote",
                  "enabled": true,
                  "url": "https://stitch.googleapis.com/mcp",
                  "headers": {
                    "Accept": "application/json",
                    "X-Goog-Api-Key": "super-secret-token"
                  }
                }
                """);
            await catalog.SaveServerAsync(parsed.Server, parsed.Headers, parsed.EnvironmentVariables);

            var discovery = await contributor.ListExportItemsAsync(new StackExportDiscoveryContext("sunder.package.agent.mcp"));
            var item = Assert.Single(discovery);
            Assert.Contains(item.Details ?? [], detail => detail.DetailId == "header.accept" && detail.Sensitivity == StackValueSensitivity.Public && detail.Value == "application/json");
            Assert.Contains(item.Details ?? [], detail => detail.DetailId == "header.x-goog-api-key" && detail.Sensitivity == StackValueSensitivity.Secret && detail.Value == "Value not exported");

            var contribution = await contributor.ExportAsync(new StackExportRequest(["server-1"]));

            var fragment = Assert.Single(contribution.Fragments);
            Assert.Contains("application/json", fragment.JsonPayload, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-token", fragment.JsonPayload, StringComparison.Ordinal);
            var requiredInput = Assert.Single(fragment.RequiredInputs ?? []);
            Assert.Contains("X-Goog-Api-Key", requiredInput.Label, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ImportFileAsync_ImportsOpenCodeAndClaudeServers()
    {
        var root = CreateTempDirectory();
        try
        {
            var context = new TestPackageContext(root);
            var catalog = new McpServerCatalogService(context);
            var importer = new McpEcosystemConfigurationImporter(catalog);
            var configPath = Path.Combine(root, "opencode.jsonc");
            await File.WriteAllTextAsync(configPath, """
                {
                  "mcp": {
                    "higgsfield": {
                      "type": "remote",
                      "url": "https://mcp.higgsfield.ai/mcp",
                      "oauth": true,
                    }
                  },
                  "mcpServers": {
                    "meshy": {
                      "command": "npx",
                      "args": ["-y", "@meshy/mcp"],
                      "env": { "MESHY_API_KEY": "secret" }
                    }
                  }
                }
                """);

            var result = await importer.ImportFileAsync(configPath);

            Assert.True(result.ImportedCount == 2, string.Join(Environment.NewLine, result.Warnings));
            var servers = await catalog.ListServersAsync();
            var higgsfield = Assert.Single(servers, server => server.Name == "higgsfield");
            Assert.True(higgsfield.OAuthEnabled);
            var meshy = Assert.Single(servers, server => server.Name == "meshy");
            Assert.Equal(["npx", "-y", "@meshy/mcp"], meshy.CommandParts);
            Assert.Equal("secret", catalog.GetEnvironmentVariables(meshy)["MESHY_API_KEY"]);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ImportSunderConfigurationFileAsync_TracksExternalSourceMetadata()
    {
        var root = CreateTempDirectory();
        try
        {
            var context = new TestPackageContext(root);
            var catalog = new McpServerCatalogService(context);
            var importer = new McpEcosystemConfigurationImporter(catalog);
            var configPath = Path.Combine(root, ".config", "sunder", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            await File.WriteAllTextAsync(configPath, """
                {
                  "mcp": {
                    "context7": {
                      "type": "remote",
                      "url": "https://mcp.context7.com/mcp",
                      "oauth": {
                        "enabled": true,
                        "scopes": ["read"]
                      }
                    }
                  }
                }
                """);

            var result = await importer.ImportSunderConfigurationFileAsync(configPath);

            Assert.Equal(1, result.ImportedCount);
            var server = Assert.Single(await catalog.ListServersAsync());
            Assert.True(server.IsExternallyManaged);
            Assert.Equal(McpEcosystemConfigurationImporter.SunderConfigurationSourceKind, server.SourceKind);
            Assert.Equal(Path.GetFullPath(configPath), server.SourceUri);
            Assert.Equal("context7", server.SourceName);
            Assert.False(string.IsNullOrWhiteSpace(server.LastImportedHash));
            Assert.True(server.OAuthEnabled);
            Assert.Equal(["read"], server.OAuthScopes);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ImportSunderConfigurationFileAsync_SkipsManualServerConflict()
    {
        var root = CreateTempDirectory();
        try
        {
            var context = new TestPackageContext(root);
            var catalog = new McpServerCatalogService(context);
            var importer = new McpEcosystemConfigurationImporter(catalog);
            var manual = McpConfigurationDocument.Parse(
                "server-1",
                "context7",
                """
                {
                  "type": "remote",
                  "url": "https://old.example.com/mcp"
                }
                """);
            await catalog.SaveServerAsync(manual.Server, manual.Headers, manual.EnvironmentVariables);
            var configPath = Path.Combine(root, ".config", "sunder", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            await File.WriteAllTextAsync(configPath, """
                {
                  "mcp": {
                    "context7": {
                      "type": "remote",
                      "url": "https://new.example.com/mcp"
                    }
                  }
                }
                """);

            var result = await importer.ImportSunderConfigurationFileAsync(configPath);

            Assert.Equal(0, result.ImportedCount);
            Assert.Equal(1, result.SkippedCount);
            Assert.Contains(result.Warnings, warning => warning.Contains("outside this Sunder config source", StringComparison.OrdinalIgnoreCase));
            var server = await catalog.GetServerAsync("server-1");
            Assert.NotNull(server);
            Assert.False(server.IsExternallyManaged);
            Assert.Equal("https://old.example.com/mcp", server.EndpointUrl);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ImportSunderConfigurationFileAsync_UpdatesSameManagedSource()
    {
        var root = CreateTempDirectory();
        try
        {
            var context = new TestPackageContext(root);
            var catalog = new McpServerCatalogService(context);
            var importer = new McpEcosystemConfigurationImporter(catalog);
            var configPath = Path.Combine(root, ".config", "sunder", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            await File.WriteAllTextAsync(configPath, """
                {
                  "mcp": {
                    "context7": {
                      "type": "remote",
                      "url": "https://old.example.com/mcp"
                    }
                  }
                }
                """);
            await importer.ImportSunderConfigurationFileAsync(configPath);
            var original = Assert.Single(await catalog.ListServersAsync());

            await File.WriteAllTextAsync(configPath, """
                {
                  "mcp": {
                    "context7": {
                      "type": "remote",
                      "url": "https://new.example.com/mcp"
                    }
                  }
                }
                """);

            var result = await importer.ImportSunderConfigurationFileAsync(configPath);

            Assert.Equal(1, result.ImportedCount);
            var updated = Assert.Single(await catalog.ListServersAsync());
            Assert.Equal(original.ServerId, updated.ServerId);
            Assert.Equal("https://new.example.com/mcp", updated.EndpointUrl);
            Assert.True(updated.IsExternallyManaged);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ImportSunderConfigurationFileAsync_RemovesManagedServersMissingFromSameSource()
    {
        var root = CreateTempDirectory();
        try
        {
            var context = new TestPackageContext(root);
            var catalog = new McpServerCatalogService(context);
            var importer = new McpEcosystemConfigurationImporter(catalog);
            var configPath = Path.Combine(root, ".config", "sunder", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            await File.WriteAllTextAsync(configPath, """
                {
                  "mcp": {
                    "one": { "type": "remote", "url": "https://one.example.com/mcp" },
                    "two": { "type": "remote", "url": "https://two.example.com/mcp" }
                  }
                }
                """);
            await importer.ImportSunderConfigurationFileAsync(configPath);

            await File.WriteAllTextAsync(configPath, """
                {
                  "mcp": {
                    "one": { "type": "remote", "url": "https://one.example.com/mcp" }
                  }
                }
                """);

            await importer.ImportSunderConfigurationFileAsync(configPath);

            var server = Assert.Single(await catalog.ListServersAsync());
            Assert.Equal("one", server.Name);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SunderConfigurationSyncService_SyncsGlobalAndWorkspaceConfigurations()
    {
        var root = CreateTempDirectory();
        try
        {
            var home = Path.Combine(root, "home");
            var project = Path.Combine(root, "project");
            Directory.CreateDirectory(project);
            var globalConfigPath = Path.Combine(home, ".config", "sunder", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(globalConfigPath)!);
            await File.WriteAllTextAsync(globalConfigPath, """
                {
                  "mcp": {
                    "global_server": { "type": "remote", "url": "https://global.example.com/mcp" }
                  }
                }
                """);
            var workspaceConfigPath = Path.Combine(project, ".sunder", "mcp.json");
            Directory.CreateDirectory(Path.GetDirectoryName(workspaceConfigPath)!);
            await File.WriteAllTextAsync(workspaceConfigPath, """
                {
                  "mcp": {
                    "workspace_server": { "type": "local", "command": ["npx", "-y", "workspace-mcp"] }
                  }
                }
                """);

            var context = new TestPackageContext(Path.Combine(root, "package"));
            var catalog = new McpServerCatalogService(context);
            var importer = new McpEcosystemConfigurationImporter(catalog);
            var workspace = CreateWorkspace(project);
            var syncService = new McpSunderConfigurationSyncService(
                importer,
                new TestExtensionCatalog(new TestRuntimeCatalog([workspace])),
                userProfilePath: home);

            var result = await syncService.SyncAsync();

            Assert.Equal(2, result.ImportedCount);
            var servers = await catalog.ListServersAsync();
            Assert.Contains(servers, server => server.Name == "global_server" && server.EndpointUrl == "https://global.example.com/mcp");
            Assert.Contains(servers, server => server.Name == "workspace_server" && server.CommandParts.SequenceEqual(["npx", "-y", "workspace-mcp"]));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string ReadFirstSecretName(string jsonPayload, string propertyName)
    {
        using var document = JsonDocument.Parse(jsonPayload);
        var references = document.RootElement.GetProperty(propertyName);
        return references[0].GetProperty("name").GetString() ?? string.Empty;
    }

    private static StackFragmentImport ToImportFragment(StackFragmentExport fragment)
        => new(
            fragment.FragmentId,
            "sunder.package.agent.mcp",
            fragment.ContributorId,
            fragment.SchemaId,
            fragment.SchemaVersion,
            fragment.DisplayName,
            fragment.JsonPayload,
            fragment.Description,
            fragment.Files?.Select(file => new StackImportPayloadFile(file.RelativePath, file.SourcePath)).ToArray());

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-agent-mcp-stack-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static AgentWorkspaceRecord CreateWorkspace(string hostPath)
    {
        var now = DateTimeOffset.UtcNow;
        const string workspaceId = "workspace-1";
        return new AgentWorkspaceRecord(
            workspaceId,
            "Workspace",
            Description: null,
            now,
            now,
            [new AgentWorkspacePathRecord("path-1", workspaceId, hostPath, IsDefault: true, SortOrder: 0, now, now)]);
    }

    private sealed class TestExtensionCatalog(params IAgentRuntimeCatalog[] runtimeCatalogs) : IPackageExtensionCatalog
    {
        public IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
            => typeof(TContract) == typeof(IAgentRuntimeCatalog)
                ? runtimeCatalogs.Cast<TContract>().ToArray()
                : [];
    }

    private sealed class TestRuntimeCatalog(IReadOnlyList<AgentWorkspaceRecord> workspaces) : IAgentRuntimeCatalog
    {
        public event Action<Guid>? SessionChanged;

        public event Action<Guid, AgentTurnRecord>? TurnChanged;

        public event Action<string>? ProfileChanged;

        public IReadOnlyList<AgentSessionRecord> ListSessions() => [];

        public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId) => [];

        public IReadOnlyList<AgentSessionRecord> ListSessionsForWorkspace(string workspaceId) => [];

        public AgentSessionRecord? GetSession(Guid sessionId) => null;

        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => workspaces;

        public AgentWorkspaceRecord? GetWorkspace(string workspaceId)
            => workspaces.FirstOrDefault(workspace => string.Equals(workspace.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase));

        public AgentProfileRecord? GetSessionProfile(Guid sessionId) => null;

        public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => null;

        public AgentSessionContextCheckpointRecord? GetLatestSessionContextCheckpoint(Guid sessionId) => null;

        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;

        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => [];

        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit) => [];

        public IReadOnlyList<AgentTurnRecord> ListTurnsAfter(Guid sessionId, DateTimeOffset afterCreatedAtUtc, Guid afterTurnId, int limit) => [];

        public IReadOnlyList<AgentProfileRecord> ListProfiles() => [];

        public AgentProfileRecord? GetProfile(string profileId) => null;

        public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind) => null;

        public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind) => null;

        public void RaiseUnusedEvents()
        {
            SessionChanged?.Invoke(Guid.Empty);
            TurnChanged?.Invoke(Guid.Empty, null!);
            ProfileChanged?.Invoke(string.Empty);
        }
    }

    private sealed class TestPackageContext(string rootPath) : IPackageContext
    {
        public string PackageId => "sunder.package.agent.mcp";

        public Version Version { get; } = new(1, 2, 3);

        public string InstallPath => AppContext.BaseDirectory;

        public IPackageStorageContext Storage { get; } = new TestStorageContext(rootPath);

        public IPackageConfiguration Configuration { get; } = new TestConfiguration();

        public IPackageSecrets Secrets { get; } = new TestSecrets();

        public ILoggerFactory LoggerFactory => Logging.LoggerFactory;

        public IPackageLogging Logging { get; } = NullPackageLogging.Instance;
    }

    private sealed class TestStorageContext : IPackageStorageContext
    {
        public TestStorageContext(string rootPath)
        {
            DataRootPath = Path.Combine(rootPath, "data");
            CacheRootPath = Path.Combine(rootPath, "cache");
            LogsRootPath = Path.Combine(rootPath, "logs");
            Directory.CreateDirectory(DataRootPath);
            Directory.CreateDirectory(CacheRootPath);
            Directory.CreateDirectory(LogsRootPath);
            Files = new TestFileStore(Path.Combine(rootPath, "files"));
            State = new TestKeyValueStore();
        }

        public string DataRootPath { get; }

        public string CacheRootPath { get; }

        public string LogsRootPath { get; }

        public IPackageFileStore Files { get; }

        public IPackageKeyValueStore State { get; }
    }

    private sealed class TestFileStore(string rootPath) : IPackageFileStore
    {
        public string RootPath { get; } = rootPath;

        public string GetPath(string relativePath)
            => string.IsNullOrWhiteSpace(relativePath)
                ? RootPath
                : Path.Combine([RootPath, .. relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);
    }

    private sealed class TestKeyValueStore : IPackageKeyValueStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? GetValue(string key) => _values.GetValueOrDefault(key);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(GetValue(key));

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.ContainsKey(key));

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_values.Keys.Where(key => prefix is null || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    private sealed class TestConfiguration : IPackageConfiguration
    {
        public string? GetValue(string key) => null;
    }

    private sealed class TestSecrets : IPackageSecrets
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? GetSecret(string key) => _values.GetValueOrDefault(key);

        public void SetSecret(string key, string value)
        {
            _values[key] = value;
        }

        public void DeleteSecret(string key)
        {
            _values.Remove(key);
        }
    }
}
