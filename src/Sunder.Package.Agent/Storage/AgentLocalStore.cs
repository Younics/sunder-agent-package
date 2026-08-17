using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Shared.Storage;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore : IDisposable, IAsyncDisposable
{
    internal const string UnassignedSessionsWorkspaceId = "legacy-unassigned-sessions";
    internal const string UnassignedSessionsWorkspaceDisplayName = "Unassigned Sessions";

    private readonly SqliteWalCheckpointMaintenance _walCheckpointMaintenance;

    public AgentLocalStore(IPackageContext packageContext)
    {
        EnsureSqliteNativeLibraryLoaded(packageContext.ContentRootPath);
        SQLitePCL.Batteries_V2.Init();

        DatabasePath = packageContext.Storage.RoleLocalWorkspace.GetLocalPath("agent/agent.db");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        ApplySchemaMigrations();
        _walCheckpointMaintenance = new SqliteWalCheckpointMaintenance(
            DatabasePath,
            "Agent store",
            packageContext.Logging.LoggerFactory.CreateLogger<AgentLocalStore>());
        _walCheckpointMaintenance.ScheduleCheckpoint();
    }

    public string DatabasePath { get; }

    internal int WalCheckpointAttemptCount => _walCheckpointMaintenance.AttemptCount;

    internal Task<bool> WaitForWalCheckpointAttemptAfterAsync(
        int attemptCount,
        CancellationToken cancellationToken = default)
        => _walCheckpointMaintenance.WaitForAttemptAfterAsync(attemptCount, cancellationToken);

    internal Task WaitForWalCheckpointIdleAsync(CancellationToken cancellationToken = default)
        => _walCheckpointMaintenance.WaitForIdleAsync(cancellationToken);

    internal void RetryWalCheckpoint() => _walCheckpointMaintenance.ScheduleCheckpoint();

    public ValueTask DisposeAsync() => _walCheckpointMaintenance.DisposeAsync();

    public void Dispose() => _walCheckpointMaintenance.Dispose();
}
