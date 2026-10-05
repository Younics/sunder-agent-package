extern alias FilesWorker;
extern alias WebWorker;

using System.Reflection;
using FilesWorker::Sunder.Package.Agent.Tools.Files;
using WebWorker::Sunder.Package.Agent.Tools.Web;
using WebWorker::Sunder.Package.Agent.Tools.Web.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class ToolPackageRoleCompositionTests
{
    public static TheoryData<string, string[]> ToolPackages => new()
    {
        {
            "Sunder.Package.Agent.Tools.Files",
            [
                "rpc-provider:files.tools",
                "rpc-provider:files.permissions",
                "rpc-provider:files.prompt.context",
                "rpc-provider:files.session.cleaner",
            ]
        },
        {
            "Sunder.Package.Agent.Tools.Shell",
            [
                "rpc-provider:shell.tools",
                "rpc-provider:shell.permissions",
                "rpc-provider:shell.prompt.context",
            ]
        },
        {
            "Sunder.Package.Agent.Tools.Web",
            [
                "settings-schema",
                "rpc-provider:web.tools",
            ]
        },
    };

    [Theory]
    [InlineData("Sunder.Package.Agent.Tools.Files")]
    [InlineData("Sunder.Package.Agent.Tools.Shell")]
    [InlineData("Sunder.Package.Agent.Tools.Web")]
    public void ToolPackage_ExposesOnlyWorkerRole(string assemblyName)
    {
        var moduleTypes = Assembly.Load(assemblyName).GetTypes()
            .Where(static type => !type.IsAbstract)
            .ToArray();

        Assert.DoesNotContain(moduleTypes, typeof(ISunderAppPackageModule).IsAssignableFrom);
        Assert.NotNull(Assembly.Load(assemblyName).EntryPoint);
    }

    [Theory]
    [MemberData(nameof(ToolPackages))]
    [Trait("Lane", "PackageCompatibility")]
    public void WorkerRole_GeneratesExactContributions(string assemblyName, string[] expectedRegistrations)
    {
        var package = Assert.Single(
            AgentPackageRepositoryInventory.GetRuntimePackageProjects(),
            package => package.Name == assemblyName);
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(package.ManifestPath));
        var actual = manifest.RootElement.GetProperty("provides")
            .EnumerateArray()
            .Select(static provider => "rpc-provider:" + provider.GetProperty("providerId").GetString())
            .ToList();
        if (manifest.RootElement.GetProperty("targets")[0].GetProperty("requiredHostCapabilities")
            .EnumerateArray()
            .Any(static capability => capability.GetString() == "settings.schema.v1"))
        {
            actual.Add("settings-schema");
        }

        Assert.Equal(expectedRegistrations.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FilesComposition_DefersDurableWorkspaceCreationUntilActivation()
    {
        using var packageScope = RegressionTestPackageScope.Create();
        var claimsRoot = packageScope.Context.Storage.RoleLocalWorkspace.GetLocalPath(
            "files/scoped-instruction-claims");
        var source = new FilesToolSource(packageScope.Context);

        Assert.False(Directory.Exists(claimsRoot));

        source.Activate();

        Assert.True(Directory.Exists(claimsRoot));
    }

    [Fact]
    public async Task WebSettings_ReadSdkSettingsAndSecrets()
    {
        using var packageScope = RegressionTestPackageScope.Create();
        var settings = new RecordingSettings("7");
        var secrets = new RecordingSecrets("exa-key");
        var context = new TestPackageContext(packageScope.Context, settings, secrets);
        var service = new WebToolsSettingsService(context);

        Assert.Same(WebToolsConfiguration.Schema, ((Sunder.Sdk.Worker.ISunderWorkerSettingsSchemaProvider)service).SettingsSchema);
        Assert.Empty(settings.ReadKeys); Assert.Empty(secrets.ReadKeys);
        Assert.Equal(7, await service.GetDefaultMaxResultsAsync());
        Assert.Equal("exa-key", await service.GetExaApiKeyAsync());
        Assert.Equal(["search.maxResults.default"], settings.ReadKeys);
        Assert.Equal(["search.exa.apiKey"], secrets.ReadKeys);
    }

    private sealed class TestPackageContext(
        IPackageContext inner,
        IPackageSettings settings,
        IPackageSecrets secrets) : IPackageContext
    {
        public string PackageId => inner.PackageId;
        public string Version => inner.Version;
        public string ContentRootPath => inner.ContentRootPath;
        public IPackageStorageContext Storage => inner.Storage;
        public IPackageSettings Settings => settings;
        public IPackageSecrets Secrets => secrets;
        public IPackageLogging Logging => inner.Logging;
    }

    private sealed class RecordingSettings(string? value) : IPackageSettings
    {
        public List<string> ReadKeys { get; } = [];

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            ReadKeys.Add(key);
            return Task.FromResult(value);
        }

        public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
            => GetValueAsync(key, cancellationToken);

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingSecrets(string? value) : IPackageSecrets
    {
        public List<string> ReadKeys { get; } = [];

        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            ReadKeys.Add(key);
            return Task.FromResult(value);
        }

        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            throw new NotSupportedException();
        }

        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            TestPackageStorageGuards.Key(key);
            throw new NotSupportedException();
        }
    }

    private sealed class MutableSettings : IPackageSettings
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
            => GetStoredValueAsync(key, cancellationToken);

        public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
