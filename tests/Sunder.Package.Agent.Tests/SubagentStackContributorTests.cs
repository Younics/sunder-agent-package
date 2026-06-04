using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class SubagentStackContributorTests
{
    [Fact]
    public async Task ExportAsync_EmitsSubagentFragment()
    {
        var root = CreateTempDirectory();
        try
        {
            var context = new TestPackageContext(root);
            var service = new SubagentService(new SubagentStore(context));
            var subagent = service.CreateSubagent("Reviewer");
            service.SaveSubagent(
                subagent.SubagentId,
                "Reviewer",
                "Reviews code changes.",
                "Focus on bugs and regressions.",
                "openai",
                "gpt-5.5",
                [new AgentProfileSelectableCapabilityAssignmentRecord("skill", "review", "skills")],
                "{\"temperature\":0.1}");
            var contributor = new SubagentStackContributor(service, context);

            var contribution = await contributor.ExportAsync(new StackExportRequest([subagent.SubagentId]));

            var fragment = Assert.Single(contribution.Fragments);
            Assert.Equal("sunder.package.agent.subagents", Assert.Single(contribution.PackageRequirements).PackageId);
            Assert.Contains("Focus on bugs", fragment.JsonPayload, StringComparison.Ordinal);
            Assert.Contains("gpt-5.5", fragment.JsonPayload, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ImportAsync_CreatesSubagentDefinition()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourceContext = new TestPackageContext(Path.Combine(root, "source"));
            var sourceService = new SubagentService(new SubagentStore(sourceContext));
            var subagent = sourceService.CreateSubagent("Researcher");
            serviceSaveResearcher(sourceService, subagent.SubagentId);
            var sourceContributor = new SubagentStackContributor(sourceService, sourceContext);
            var fragment = Assert.Single((await sourceContributor.ExportAsync(new StackExportRequest([subagent.SubagentId]))).Fragments);

            var targetContext = new TestPackageContext(Path.Combine(root, "target"));
            var targetService = new SubagentService(new SubagentStore(targetContext));
            var targetContributor = new SubagentStackContributor(targetService, targetContext);
            var importFragment = ToImportFragment(fragment);
            var preview = await targetContributor.PreviewImportAsync(new StackImportPreviewRequest(
                [importFragment],
                new Dictionary<string, string>(),
                new Dictionary<string, string>()));
            var action = Assert.Single(preview.Actions);

            var result = await targetContributor.ImportAsync(new StackImportRequest(
                [importFragment],
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                [action.ActionId]));

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
            var imported = targetService.GetSubagent(subagent.SubagentId);
            Assert.NotNull(imported);
            Assert.Equal("Researcher", imported.DisplayName);
            Assert.Equal("Researches a topic.", imported.Description);
            Assert.Equal("Use citations.", imported.Instructions);
            Assert.Equal("anthropic", imported.ChatProviderId);
            Assert.Equal("claude", imported.ChatModelId);
        }
        finally
        {
            TryDeleteDirectory(root);
        }

        static void serviceSaveResearcher(SubagentService service, string subagentId)
            => service.SaveSubagent(
                subagentId,
                "Researcher",
                "Researches a topic.",
                "Use citations.",
                "anthropic",
                "claude",
                []);
    }

    private static StackFragmentImport ToImportFragment(StackFragmentExport fragment)
        => new(
            fragment.FragmentId,
            "sunder.package.agent.subagents",
            fragment.ContributorId,
            fragment.SchemaId,
            fragment.SchemaVersion,
            fragment.DisplayName,
            fragment.JsonPayload,
            fragment.Description,
            fragment.Files?.Select(file => new StackImportPayloadFile(file.RelativePath, file.SourcePath)).ToArray());

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-subagent-stack-tests", Guid.NewGuid().ToString("N"));
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
        public string PackageId => "sunder.package.agent.subagents";

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
        public string? GetSecret(string key) => null;

        public void SetSecret(string key, string value)
        {
        }

        public void DeleteSecret(string key)
        {
        }
    }
}
