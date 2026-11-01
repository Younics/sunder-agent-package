using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tests;

internal sealed class RegressionTestPackageScope : IDisposable
{
    private readonly TemporaryDirectory _temporaryDirectory;

    private RegressionTestPackageScope(TemporaryDirectory temporaryDirectory)
    {
        _temporaryDirectory = temporaryDirectory;
        RootPath = temporaryDirectory.Path;
        Context = new RegressionTestPackageContext(RootPath);
    }

    public string RootPath { get; }

    public IPackageContext Context { get; }

    public static RegressionTestPackageScope Create()
        => new(TemporaryDirectory.Create("sunder-agent-regression-tests"));

    public void Dispose() => _temporaryDirectory.Dispose();
}
