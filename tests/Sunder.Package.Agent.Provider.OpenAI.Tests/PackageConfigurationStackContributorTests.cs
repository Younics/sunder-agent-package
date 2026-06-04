using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Shared.Stacks;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class PackageConfigurationStackContributorTests
{
    [Fact]
    public async Task ExportAsync_OmitsSecretValuesAndEmitsRequiredInput()
    {
        var context = new TestPackageContext();
        await context.Storage.State.SetValueAsync(OpenAiProviderConfiguration.UtilityModelKey, "openai/gpt-5.5");
        context.Secrets.SetSecret("auth.apiKey", "sk-test-secret");
        var contributor = new PackageConfigurationStackContributor(OpenAiProviderConfiguration.Schema, context);

        var contribution = await contributor.ExportAsync(new StackExportRequest(["settings"]));

        var fragment = Assert.Single(contribution.Fragments);
        Assert.DoesNotContain("sk-test-secret", fragment.JsonPayload, StringComparison.Ordinal);
        Assert.Contains("openai/gpt-5.5", fragment.JsonPayload, StringComparison.Ordinal);
        var input = Assert.Single(fragment.RequiredInputs ?? []);
        Assert.Equal("API key", input.Label);
    }

    [Fact]
    public async Task ImportAsync_AppliesNonSecretValuesAndSuppliedSecrets()
    {
        var sourceContext = new TestPackageContext();
        await sourceContext.Storage.State.SetValueAsync(OpenAiProviderConfiguration.UtilityModelKey, "openai/gpt-5.5");
        sourceContext.Secrets.SetSecret("auth.apiKey", "sk-source-secret");
        var sourceContributor = new PackageConfigurationStackContributor(OpenAiProviderConfiguration.Schema, sourceContext);
        var fragment = Assert.Single((await sourceContributor.ExportAsync(new StackExportRequest(["settings"]))).Fragments);

        var targetContext = new TestPackageContext();
        var targetContributor = new PackageConfigurationStackContributor(OpenAiProviderConfiguration.Schema, targetContext);
        var importFragment = ToImportFragment(fragment);
        var preview = await targetContributor.PreviewImportAsync(new StackImportPreviewRequest(
            [importFragment],
            new Dictionary<string, string>(),
            new Dictionary<string, string>()));
        var action = Assert.Single(preview.Actions);
        var input = Assert.Single(preview.RequiredInputs);

        var result = await targetContributor.ImportAsync(new StackImportRequest(
            [importFragment],
            new Dictionary<string, string> { [input.InputId] = "sk-target-secret" },
            new Dictionary<string, string>(),
            [action.ActionId]));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("openai/gpt-5.5", targetContext.Storage.State.GetValue(OpenAiProviderConfiguration.UtilityModelKey));
        Assert.Equal("sk-target-secret", targetContext.Secrets.GetSecret("auth.apiKey"));
    }

    private static StackFragmentImport ToImportFragment(StackFragmentExport fragment)
        => new(
            fragment.FragmentId,
            "sunder.package.agent.provider.openai",
            fragment.ContributorId,
            fragment.SchemaId,
            fragment.SchemaVersion,
            fragment.DisplayName,
            fragment.JsonPayload,
            fragment.Description,
            fragment.Files?.Select(file => new StackImportPayloadFile(file.RelativePath, file.SourcePath)).ToArray());

    private sealed class TestPackageContext : IPackageContext
    {
        public string PackageId => "sunder.package.agent.provider.openai";

        public Version Version { get; } = new(1, 2, 3);

        public string InstallPath => AppContext.BaseDirectory;

        public IPackageStorageContext Storage { get; } = new TestStorageContext();

        public IPackageConfiguration Configuration => new TestConfiguration(Storage.State);

        public IPackageSecrets Secrets { get; } = new TestSecrets();

        public ILoggerFactory LoggerFactory => Logging.LoggerFactory;

        public IPackageLogging Logging { get; } = NullPackageLogging.Instance;
    }

    private sealed class TestStorageContext : IPackageStorageContext
    {
        public string DataRootPath { get; } = Path.GetTempPath();

        public string CacheRootPath { get; } = Path.GetTempPath();

        public string LogsRootPath { get; } = Path.GetTempPath();

        public IPackageFileStore Files { get; } = new TestFileStore();

        public IPackageKeyValueStore State { get; } = new TestKeyValueStore();
    }

    private sealed class TestFileStore : IPackageFileStore
    {
        public string RootPath => Path.GetTempPath();

        public string GetPath(string relativePath) => Path.Combine(RootPath, relativePath);
    }

    private sealed class TestConfiguration(IPackageKeyValueStore state) : IPackageConfiguration
    {
        public string? GetValue(string key) => state.GetValue(key);
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
