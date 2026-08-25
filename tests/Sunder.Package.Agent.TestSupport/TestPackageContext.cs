using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.TestSupport;

public sealed class TestPackageContext : IPackageContext
{
    public TestPackageContext(
        string packageId,
        string? workspaceRoot = null,
        string version = "1.0.0",
        string? contentRootPath = null,
        IPackageStorageContext? storage = null,
        IPackageSettings? settings = null,
        IPackageSecrets? secrets = null,
        IPackageCallbackClient? callbacks = null,
        IPackageLogging? logging = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        PackageId = packageId;
        Version = version;
        ContentRootPath = System.IO.Path.GetFullPath(contentRootPath ?? AppContext.BaseDirectory);
        var root = System.IO.Path.GetFullPath(workspaceRoot ?? AppContext.BaseDirectory);
        Storage = storage ?? new TestPackageStorageContext(
            UnsupportedPackageFileStore.Instance,
            new InMemoryPackageKeyValueStore(),
            new TestPackageRoleLocalWorkspace(root));
        Settings = settings ?? new InMemoryPackageSettings();
        Secrets = secrets ?? new InMemoryPackageSecrets();
        Callbacks = callbacks ?? NullPackageCallbackClient.Instance;
        Logging = logging ?? NullPackageLogging.Instance;
    }

    public string PackageId { get; }

    public string Version { get; }

    public string ContentRootPath { get; }

    public IPackageStorageContext Storage { get; }

    public IPackageSettings Settings { get; }

    public IPackageSecrets Secrets { get; }

    public IPackageCallbackClient Callbacks { get; }

    public IPackageLogging Logging { get; }

    public static TestPackageContext CreateFileBacked(
        string packageId,
        string workspaceRoot,
        IReadOnlyDictionary<string, string>? state = null,
        IReadOnlyDictionary<string, string>? settings = null,
        IReadOnlyDictionary<string, string>? secrets = null,
        IPackageLogging? logging = null)
    {
        var root = System.IO.Path.GetFullPath(workspaceRoot);
        Directory.CreateDirectory(root);
        return new TestPackageContext(
            packageId,
            root,
            storage: new TestPackageStorageContext(
                new DirectoryPackageFileStore(System.IO.Path.Combine(root, "files")),
                new InMemoryPackageKeyValueStore(state),
                new TestPackageRoleLocalWorkspace(root)),
            settings: new InMemoryPackageSettings(settings),
            secrets: new InMemoryPackageSecrets(secrets),
            logging: logging);
    }
}

public sealed class TestPackageStorageContext(
    IPackageFileStore files,
    IPackageKeyValueStore state,
    IPackageRoleLocalWorkspace roleLocalWorkspace) : IPackageStorageContext
{
    public IPackageFileStore Files { get; } = files;

    public IPackageKeyValueStore State { get; } = state;

    public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; } = roleLocalWorkspace;
}
