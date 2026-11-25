using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RuntimeProcessIntegrationCollection
{
    public const string Name = "Runtime process integration";
}

internal static class CurrentRidRuntimeHostTestSupport
{
    internal static void AssertBindingMatches(InstalledPackageExecutionRuntimeBindingRecord actual, ExecutionRuntimeReceiptV1 receipt)
    {
        var expected = new InstalledPackageExecutionRuntimeBindingRecord("runtime", receipt.Identity.Rid,
            new RuntimeExecutionRuntimeRequirement(receipt.Identity.RuntimeId, ">=10.0.0 <11.0.0", receipt.Identity.ModuleAbi),
            receipt.Identity.ToContract(), receipt.InstalledArtifact, actual.ResolvedAtUtc);
        Assert.Equal(expected, actual);
    }

    internal static void AssertRuntimeLaunch(ActiveLoadedPackage package, ExecutionRuntimeReceiptV1 receipt, IServiceProvider services)
    {
        Assert.Equal(receipt.Identity, package.ExecutionRuntimeBinding?.Identity);
        var worker = Assert.IsType<ProcessRuntimeWorker>(package.Worker);
        var process = WorkerReleaseMetrics.GetProcess(worker);
        var runtime = services.GetRequiredService<ExecutionRuntimeStoreLayout>().GetContentRootPath(receipt.ContentIdentity);
        var expected = ArchiveRelativePath.Parse(receipt.LauncherPath).ToPlatformPath(runtime);
        var executable = Assert.IsType<string>(process.MainModule?.FileName);
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(executable));
        Assert.False(Path.GetFullPath(executable).StartsWith(
            Path.GetFullPath(package.Source.SourceFolder) + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "Worker launched a runtime bundled in the package.");
    }
}

internal static class WorkerReleaseMetrics
{
    internal static Process GetProcess(object worker)
    {
        var process = Assert.IsType<Process>(worker.GetType().GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(worker));
        process.Refresh();
        Assert.False(process.HasExited);
        return process;
    }

    internal static long DirectoryBytes(string path) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);

    internal static async Task RecordAsync(string profileName, string rid, string archivePath, long currentRidInstalledBytes,
        TimeSpan startup, int workerProcessCount, Process workerProcess, string outputPath)
    {
        // These are the existing Worker V1 release budgets, not SDK tests.
        var thresholds = new
        {
            maximumArchiveBytes = 268435456L, maximumCurrentRidInstalledBytes = 536870912L,
            maximumStartupMilliseconds = 30000, expectedWorkerProcessCount = 1, maximumWorkerRssBytes = 536870912L,
        };
        await Task.Delay(250);
        workerProcess.Refresh();
        var measurement = new
        {
            formatVersion = 1, profile = profileName, rid, archive = Path.GetFileName(archivePath),
            archiveBytes = new FileInfo(archivePath).Length, currentRidInstalledBytes,
            startupMilliseconds = startup.TotalMilliseconds, workerProcessCount, workerRssBytes = workerProcess.WorkingSet64,
            capturedAtUtc = DateTimeOffset.UtcNow,
        };
        Assert.InRange(measurement.archiveBytes, 1, thresholds.maximumArchiveBytes);
        Assert.InRange(currentRidInstalledBytes, 1, thresholds.maximumCurrentRidInstalledBytes);
        Assert.InRange(measurement.startupMilliseconds, 0.001, thresholds.maximumStartupMilliseconds);
        Assert.Equal(thresholds.expectedWorkerProcessCount, workerProcessCount);
        Assert.InRange(measurement.workerRssBytes, 1, thresholds.maximumWorkerRssBytes);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new { measurement, thresholds }));
    }
}
