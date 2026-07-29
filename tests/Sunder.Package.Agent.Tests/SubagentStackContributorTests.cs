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
            var contributor = new SubagentStackContributor(service, context, new RegressionTestExtensionCatalog());

            var item = Assert.Single(await contributor.ListExportItemsAsync(
                new StackExportDiscoveryContext(context.PackageId)));
            var contribution = await contributor.ExportAsync(new StackExportRequest(
                [new StackExportItemSelection(subagent.SubagentId)]));

            var fragment = Assert.Single(contribution.Fragments);
            Assert.All(item.Details ?? [], detail => Assert.NotNull(detail.Sensitivity));
            var requirement = Assert.Single(contribution.PackageRequirements);
            Assert.Equal("sunder.package.agent.subagents", requirement.PackageId);
            Assert.Equal("1.1.0", requirement.MinimumVersion);
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
            var sourceContributor = new SubagentStackContributor(sourceService, sourceContext, new RegressionTestExtensionCatalog());
            var fragment = Assert.Single((await sourceContributor.ExportAsync(new StackExportRequest(
                [new StackExportItemSelection(subagent.SubagentId)]))).Fragments);

            var targetContext = new TestPackageContext(Path.Combine(root, "target"));
            var targetService = new SubagentService(new SubagentStore(targetContext));
            var targetContributor = new SubagentStackContributor(targetService, targetContext, new RegressionTestExtensionCatalog());
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

            Assert.Equal(StackImportOutcome.Completed, result.Outcome);
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
            "sunder.package.agent.subagents.subagents",
            fragment.SchemaId,
            fragment.SchemaVersion,
            fragment.DisplayName,
            fragment.JsonPayload,
            fragment.Description,
            fragment.Files?.Select(file => new StackImportPayloadHandle(
                file.RelativePath,
                file.OpenReadAsync,
                file.Length ?? throw new InvalidOperationException("Test export payload length is required."))).ToArray());

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

        public string Version { get; } = "1.2.3";

        public string ContentRootPath => AppContext.BaseDirectory;

        public IPackageStorageContext Storage { get; } = new TestStorageContext(rootPath);

        public IPackageSettings Settings { get; } = new TestSettings();

        public IPackageSecrets Secrets { get; } = new TestSecrets();


        public IPackageLogging Logging { get; } = NullPackageLogging.Instance;
    }

    private sealed class TestStorageContext : IPackageStorageContext
    {
        public TestStorageContext(string rootPath)
        {
            Directory.CreateDirectory(rootPath);
            Files = new TestFileStore(Path.Combine(rootPath, "files"));
            State = new TestKeyValueStore();
            RoleLocalWorkspace = new TestPackageRoleLocalWorkspace(rootPath);
        }

        public IPackageFileStore Files { get; }

        public IPackageKeyValueStore State { get; }
        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; }
    }

    private sealed class TestFileStore(string rootPath) : TestPackageFileStoreBase(rootPath);

    private sealed class TestKeyValueStore : IPackageKeyValueStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.ContainsKey(key));
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Prefix(prefix);
            return Task.FromResult<IReadOnlyList<string>>(_values.Keys
                .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray());
        }
    }

    private sealed class TestSettings : EmptyPackageSettings;

    private sealed class TestSecrets : InMemoryPackageSecrets
    {
        public string? GetSecret(string key)
        {
            TestPackageStorageGuards.Key(key);
            return null;
        }

        public void SetSecret(string key, string value)
        {
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
        }

        public void DeleteSecret(string key)
        {
            TestPackageStorageGuards.Key(key);
        }
    }
}
