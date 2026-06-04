using System.Text.Json;
using Microsoft.Extensions.Logging;
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
