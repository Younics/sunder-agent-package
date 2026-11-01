using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Tests;

// Valid immutable host metadata, but no execution/storage/settings services.
// A source-only request must never construct the SDK or job backend.
internal sealed class BuilderInspectionOnlyContext : IPackageContext
{
    private int _versionReads;
    private int _forbiddenAccesses;
    internal int VersionReads => Volatile.Read(ref _versionReads);
    internal int ForbiddenAccesses => Volatile.Read(ref _forbiddenAccesses);
    public string PackageId => "younics.sunder.package.maker";
    public string Version => "2.0.0";
    public SunderDevelopmentToolchain DevelopmentToolchain
    {
        get
        {
            Interlocked.Increment(ref _versionReads);
            return new("10.0.403", "24.22.1", "11.19.1");
        }
    }
    public string ContentRootPath => throw Forbidden();
    public IPackageStorageContext Storage => throw Forbidden();
    public IPackageSettings Settings => throw Forbidden();
    public IPackageSecrets Secrets => throw Forbidden();
    public IPackageCallbackClient Callbacks => throw Forbidden();
    public IPackageLogging Logging => NullPackageLogging.Instance;
    private InvalidOperationException Forbidden()
    {
        Interlocked.Increment(ref _forbiddenAccesses);
        return new("Source inspection must not resolve an execution or mutable host capability.");
    }
}
