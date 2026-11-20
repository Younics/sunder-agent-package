extern alias WebWorker;

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using WebWorker::Sunder.Package.Agent.Tools.Web;
using WebWorker::Sunder.Package.Agent.Tools.Web.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class ToolPackageRoleCompositionTests
{
    private const string PackageAssemblyName = "Sunder.Package.Agent.Tools.Web";
    private static string[] ExpectedRegistrations => ["settings-schema", "rpc-provider:web.tools"];

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

    [Fact]
    public async Task WebGeneratedComposition_ResolvesAutoSearchWithoutCredentialsOrNetwork()
    {
        using var packageScope = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState();
        var graph = GeneratedPackageCompositionFixture.CreateGeneratedWorker(typeof(WebSearchTool).Assembly,
            packageScope.Context, new RegressionTestRpcClient(state, packageScope.Context.PackageId));
        await using var lifetime = graph.Lifetime;

        var readiness = await graph.Services.GetRequiredService<WebSearchTool>().GetReadinessAsync();

        Assert.Contains("Parallel", readiness.Message, StringComparison.Ordinal);
        Assert.Contains("automatic rate-limit failover", readiness.Message, StringComparison.Ordinal);
        Assert.NotNull(graph.Services.GetRequiredService<WebSearchService>());
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
