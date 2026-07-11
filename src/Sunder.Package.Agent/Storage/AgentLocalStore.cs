using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Storage;

public sealed partial class AgentLocalStore
{
    internal const string UnassignedSessionsWorkspaceId = "legacy-unassigned-sessions";
    internal const string UnassignedSessionsWorkspaceDisplayName = "Unassigned Sessions";

    public AgentLocalStore(IPackageContext packageContext)
    {
        EnsureSqliteNativeLibraryLoaded(packageContext.InstallPath);
        SQLitePCL.Batteries_V2.Init();

        DatabasePath = Path.Combine(packageContext.Storage.DataRootPath, "agent.db");
        Directory.CreateDirectory(packageContext.Storage.DataRootPath);
        EnsureSchema();
        EnsureTurnItemPresentationMigration();
        EnsureTraceTelemetryRemoved();
        EnsureSessionHierarchyMigration();
        EnsureSessionWorkspaceMigration();
        EnsurePendingPermissionMigration();
        EnsureProfileSchemaMigration();
        EnsureProfileModelBindingMigration();
        EnsureFailedSessionStateMigration();
        ApplySchemaMigrations();
        RecoverInterruptedPermissionClaims();
        RecoverAmbiguousParentContinuationWork();
        RecoverUnownedActiveRuns();
    }

    public string DatabasePath { get; }

    public AgentDashboardSnapshot GetDashboardSnapshot()
    {
        using var connection = CreateConnection();
        connection.Open();

        return new AgentDashboardSnapshot(
            ListProfiles(connection),
            ListSessions(connection),
            ListRecentCheckpoints(connection),
            ListRecentMessages(connection)
        );
    }
}
