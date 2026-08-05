using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Tools.Web.Services;
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
    public void ToolPackage_ExposesOnlyRuntimeRole(string assemblyName)
    {
        var moduleTypes = Assembly.Load(assemblyName).GetTypes()
            .Where(static type => !type.IsAbstract)
            .ToArray();

        if (string.Equals(assemblyName, "Sunder.Package.Agent.Tools.Shell", StringComparison.Ordinal))
        {
            Assert.DoesNotContain(moduleTypes, typeof(ISunderRuntimePackageModule).IsAssignableFrom);
            Assert.NotNull(Assembly.Load(assemblyName).EntryPoint);
        }
        else
        {
            Assert.Single(moduleTypes, typeof(ISunderRuntimePackageModule).IsAssignableFrom);
        }
        Assert.DoesNotContain(moduleTypes, typeof(ISunderAppPackageModule).IsAssignableFrom);
    }

    [Theory]
    [MemberData(nameof(ToolPackages))]
    public async Task RuntimeRole_BuildsAndRegistersExactContributions(string assemblyName, string[] expectedRegistrations)
    {
        if (string.Equals(assemblyName, "Sunder.Package.Agent.Tools.Shell", StringComparison.Ordinal))
        {
            var package = Assert.Single(
                AgentPackageRepositoryInventory.GetRuntimePackageProjects(),
                static package => package.Name == "Sunder.Package.Agent.Tools.Shell");
            var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)).Parent?.Name ?? "Debug";
            var manifestPath = Path.Combine(
                package.DirectoryPath,
                "obj",
                configuration,
                "net10.0",
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                "sunder-package.json");
            using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
            var actual = manifest.RootElement.GetProperty("provides")
                .EnumerateArray()
                .Select(static provider => "rpc-provider:" + provider.GetProperty("providerId").GetString())
                .Order(StringComparer.Ordinal);

            Assert.Equal(expectedRegistrations.Order(StringComparer.Ordinal), actual);
            return;
        }

        using var packageScope = RegressionTestPackageScope.Create();
        var services = new ServiceCollection();
        services.AddSingleton(packageScope.Context);
        services.AddSingleton<Sunder.Package.Agent.Protocol.AgentRpcCatalog>(new RegressionTestExtensionCatalog());

        var moduleType = Assert.Single(
            Assembly.Load(assemblyName).GetTypes(),
            static type => !type.IsAbstract && typeof(ISunderRuntimePackageModule).IsAssignableFrom(type));
        var module = Assert.IsAssignableFrom<ISunderRuntimePackageModule>(Activator.CreateInstance(moduleType));
        module.ConfigureRuntimeServices(services, packageScope.Context);

        await using var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var registry = new RecordingPackageContributionRegistry();

        module.RegisterRuntimeContributions(registry, serviceProvider);

        Assert.Equal(
            expectedRegistrations.Order(StringComparer.Ordinal),
            registry.Registrations.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WebSettings_ReadSdkSettingsAndSecrets()
    {
        using var packageScope = RegressionTestPackageScope.Create();
        var settings = new RecordingSettings("7");
        var secrets = new RecordingSecrets("exa-key");
        var context = new TestPackageContext(packageScope.Context, settings, secrets);
        var service = new WebToolsSettingsService(context);

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
}
