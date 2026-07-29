using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Storage;

namespace Sunder.Package.Agent.Services;

internal sealed class AgentPackageStorageMigration(IPackageContext packageContext) : IPackageBackgroundService
{
    private static readonly PackageStorageKeyMigration[] Migrations =
    [
        PackageStorageKeyMigration.OpaqueId(
            "agent.chat.selectedSessionId.",
            string.Empty,
            "agent.chat.selected-session",
            1),
    ];

    private readonly object _syncRoot = new();
    private Task? _migration;

    public Task StartAsync(CancellationToken cancellationToken = default)
        => EnsureAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    internal Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        Task migration;
        lock (_syncRoot)
        {
            migration = _migration ??= packageContext.Storage.State is IPackageStorageKeyMigrator migrator
                ? migrator.MigrateKeysAsync(Migrations, CancellationToken.None)
                : Task.CompletedTask;
        }

        return cancellationToken.CanBeCanceled ? migration.WaitAsync(cancellationToken) : migration;
    }
}
