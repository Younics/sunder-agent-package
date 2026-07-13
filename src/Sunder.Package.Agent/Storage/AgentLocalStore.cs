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

        DatabasePath = packageContext.Storage.RoleLocalWorkspace.GetLocalPath("agent/agent.db");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
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
