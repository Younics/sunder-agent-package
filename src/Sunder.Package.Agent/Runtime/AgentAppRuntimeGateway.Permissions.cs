namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentAppRuntimeGateway
{
    private AgentPermissionProjection? _globalPermissions;
    private long _globalPermissionRevision = -1;
    private int _globalPermissionGeneration;

    private AgentPermissionProjection ReadGlobalPermissionsSnapshot()
    {
        lock (_cacheLock)
        {
            return _globalPermissions
                   ?? throw new InvalidOperationException(
                       "Agent permission data must be loaded asynchronously before it is read.");
        }
    }

    private int CaptureGlobalPermissionGeneration()
    {
        lock (_cacheLock)
        {
            return _globalPermissionGeneration;
        }
    }

    private void TryCacheGlobalPermissions(
        AgentPermissionProjection projection,
        int generation)
    {
        lock (_cacheLock)
        {
            if (_disposed
                || generation != _globalPermissionGeneration
                || projection.Revision < _globalPermissionRevision)
            {
                return;
            }

            _globalPermissions = projection;
            _globalPermissionRevision = projection.Revision;
        }
    }

    private void InvalidateGlobalPermissions(long revision)
    {
        lock (_cacheLock)
        {
            if (_globalPermissions is not null
                && _globalPermissionRevision >= revision)
            {
                return;
            }

            _globalPermissions = null;
            _globalPermissionRevision = Math.Max(_globalPermissionRevision, revision);
        }
    }
}
