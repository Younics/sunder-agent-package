using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tests;

internal sealed class RegressionTestPackageContext(string rootPath) : IPackageContext
{
    public string PackageId => "test.package.agent";

    public string Version { get; } = "1.0.0";

    public string ContentRootPath => AppContext.BaseDirectory;

    public IPackageStorageContext Storage { get; } = new RegressionTestStorageContext(rootPath);

    public IPackageSettings Settings { get; } = new RegressionTestSettings();

    public IPackageSecrets Secrets { get; } = new RegressionTestSecrets();

    public Sunder.Sdk.Logging.IPackageLogging Logging { get; } =
        Sunder.Sdk.Logging.NullPackageLogging.Instance;
}

internal sealed class RegressionTestStorageContext : IPackageStorageContext
{
    public RegressionTestStorageContext(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        Files = new RegressionTestFileStore(Path.Combine(rootPath, "files"));
        RoleLocalWorkspace = new TestPackageRoleLocalWorkspace(rootPath);
    }

    public IPackageFileStore Files { get; }

    public IPackageKeyValueStore State { get; } = new RegressionTestKeyValueStore();

    public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; }
}

internal sealed class RegressionTestFileStore(string rootPath) : DirectoryPackageFileStore(rootPath);

internal sealed class RegressionTestKeyValueStore : InMemoryPackageKeyValueStore;

internal sealed class RegressionTestSettings : NoOpPackageSettings;

internal sealed class RegressionTestSecrets : InMemoryPackageSecrets;
