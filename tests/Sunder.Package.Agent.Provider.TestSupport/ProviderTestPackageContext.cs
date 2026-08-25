using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Provider.TestSupport;

public sealed class ProviderTestPackageContext : IPackageContext
{
    public ProviderTestPackageContext(
        string packageId,
        IReadOnlyDictionary<string, string>? configurationValues = null,
        IReadOnlyDictionary<string, string>? secretValues = null,
        IPackageKeyValueStore? state = null,
        IPackageSecrets? secrets = null,
        IPackageKeyValueStore? settings = null,
        PackageSettingsSchema? settingsSchema = null,
        IPackageCallbackClient? callbacks = null,
        IPackageLogging? logging = null)
    {
        PackageId = packageId;
        Storage = new ProviderTestStorageContext(state);
        Settings = new ProviderTestSettings(
            settings ?? new ProviderTestKeyValueStore(configurationValues),
            settingsSchema);
        Secrets = secrets ?? new ProviderTestSecrets(secretValues);
        Callbacks = callbacks ?? NullPackageCallbackClient.Instance;
        Logging = logging ?? NullPackageLogging.Instance;
    }

    public string PackageId { get; }

    public string Version { get; } = "1.0.0";

    public string ContentRootPath { get; } = AppContext.BaseDirectory;

    public ProviderTestStorageContext Storage { get; }

    IPackageStorageContext IPackageContext.Storage => Storage;

    public IPackageSettings Settings { get; }

    public IPackageSecrets Secrets { get; }

    public IPackageCallbackClient Callbacks { get; }


    public IPackageLogging Logging { get; }
}

public sealed class ProviderTestStorageContext : IPackageStorageContext
{
    public ProviderTestStorageContext(
        IPackageKeyValueStore? state = null)
    {
        State = state ?? new ProviderTestKeyValueStore();
    }

    public IPackageFileStore Files => throw new NotSupportedException();

    public IPackageKeyValueStore State { get; }

    IPackageKeyValueStore IPackageStorageContext.State => State;

    public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; } = new ProviderTestWorkspace();
}

public sealed class ProviderTestKeyValueStore : InMemoryPackageKeyValueStore
{
    public ProviderTestKeyValueStore(IReadOnlyDictionary<string, string>? values = null)
        : base(values)
    {
    }
}

public sealed class ProviderTestSettings(
    IPackageKeyValueStore settings,
    PackageSettingsSchema? schema = null) : InMemoryPackageSettings(settings, schema)
{
}

public sealed class ProviderTestSecrets : InMemoryPackageSecrets
{
    public ProviderTestSecrets(IReadOnlyDictionary<string, string>? values = null)
        : base(values)
    {
    }
}

internal sealed class ProviderTestWorkspace : TestPackageRoleLocalWorkspace
{
    public ProviderTestWorkspace()
        : base(AppContext.BaseDirectory)
    {
    }
}
