using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class AgentNativeReleaseSmokeTests
{
    [NativeReleaseFact]
    [Trait("Category", "Release")]
    [Trait("Capability", "Native")]
    [Trait("Size", "Large")]
    public async Task CurrentRidArchives_ExerciseEveryNativeWorkerThroughRuntimeHost()
    {
        var archives = ReadArchives(RequiredMakerPath("SUNDER_AGENT_NATIVE_ARCHIVE_DIR", directory: true), PackageTargetSelection.GetCurrentRuntimeIdentifier());
        var metrics = RequiredMakerPath("SUNDER_AGENT_NATIVE_METRICS_DIR", directory: true);
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        await VerifyMcpAppJsonHighlightingAsync(archives[McpPackageId], rid, metrics);
        var root = Path.Combine(ResolvePhysicalDirectory(Path.GetTempPath()), "sunder-native-development-" + Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(Path.Combine(root, "runtime"));
        RuntimeLocalState.EnsureInitialized(paths.RootPath);
        var receipt = await executionRuntimes.EnsureDotNetAsync(WorkerRuntimeVersion);
        var runtimeHost = CreateNativeRuntimeHost(paths, executionRuntimes, out var diagnostics, out var appConnection);
        var services = runtimeHost.Services;
        var owner = services.GetRequiredService<RuntimeSessionOwner>();
        var sessions = services.GetRequiredService<PackageSessionLifecycleService>();
        var installed = services.GetRequiredService<InstalledPackageLifecycleService>();
        var observed = new HashSet<ExecutionRuntimeProcessObservation>();
        var temporary = new HashSet<string>(StringComparer.Ordinal);
        var initialized = false;
        try
        {
            await StartNativeRuntimeHostAsync(runtimeHost, appConnection);
            await installed.InitializeAsync();
            initialized = true;
            var folders = new Dictionary<string, string>(StringComparer.Ordinal);
            var projections = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (packageId, archive) in archives)
            {
                var folder = Path.Combine(root, "sources", packageId);
                var validation = await PrepareNativeDevelopmentPackageAsync(services, archive, folder, archives.Values.ToArray());
                folders.Add(packageId, folder);
                // Use Core's actual current-RID install projection for the size
                // diagnostic, without inventing installed state or Registry policy.
                var projection = Path.Combine(root, "size-projections", packageId);
                var projected = await PackageProjectionMaterializer.MaterializeCurrentRidInstallPackageAsync(
                    folder, projection, validation.Manifest!, validation.ContentIndex!, rid, CancellationToken.None);
                Assert.True(projected.Success, string.Join(Environment.NewLine, projected.Errors));
                projections.Add(packageId, projection);
            }
            var loaded = await sessions.LoadStartupDevPackagesAsync(folders.Values.ToArray());
            CaptureWorkers(owner, observed, temporary);
            Assert.True(loaded.Success, string.Join(Environment.NewLine, loaded.Errors) + string.Join(Environment.NewLine,
                archives.Keys.Select(id => id + ": " + diagnostics.DescribeStandardError(id))));
            Assert.Empty(await installed.GetInstalledAsync());
            AssertSamePackageIds(archives.Keys, GetWorkers(owner).Keys);
            Assert.All(owner.State.ActiveSession.LoadedPackageMap.Values, package =>
            {
                Assert.Equal(PackageSourceKind.Dev, package.Source.Kind);
                Assert.True(package.Source.DevelopmentImports!.IsLocal);
                CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(package, receipt, executionRuntimes);
            });
            // Observe the staged replacement before activation, preserving the
            // real process-start-to-activation timing boundary used by the budget.
            var initial = CaptureWorkers(owner, observed, temporary);
            var stage = await sessions.StageAsync(new PackageLifecycleStageRequest(archives.Keys.ToArray()));
            Assert.True(stage.Success, string.Join(Environment.NewLine, stage.Errors));
            var staged = GetWorkers(Assert.IsType<ActivePackageSession>(sessions.GetStagedSession(stage.StageId!)));
            AssertSamePackageIds(archives.Keys, staged.Keys);
            var measurements = staged.ToDictionary(pair => pair.Key, pair => CaptureActivationMeasurement(pair.Value));
            var reload = await sessions.CommitStageAsync(stage.StageId!);
            CaptureWorkers(owner, observed, temporary);
            Assert.True(reload.Success, string.Join(Environment.NewLine, reload.Errors));
            await AssertWorkersStoppedAsync(initial);
            await RecordWorkerMetricsAsync(GetWorkers(owner), projections, archives, rid, measurements, metrics, observed, temporary);
            VerifyDatabases();
            owner.MarkReady();
            await VerifyOwnerUnaryAndStreamAsync(services, owner, rid, appConnection, root);
            await VerifyCallbackAndAuthAsync(services, owner);
            var crashed = await VerifyCrashIsolationAsync(owner, observed, temporary);
            var beforeUnload = CaptureWorkers(owner, observed, temporary);
            var unload = await sessions.LoadStartupDevPackagesAsync([]);
            Assert.True(unload.Success, string.Join(Environment.NewLine, unload.Errors));
            Assert.Empty(GetWorkers(owner));
            await AssertWorkersStoppedAsync(beforeUnload);
            var recovered = await sessions.LoadStartupDevPackagesAsync(folders.Values.ToArray());
            CaptureWorkers(owner, observed, temporary);
            Assert.True(recovered.Success, string.Join(Environment.NewLine, recovered.Errors));
            AssertSamePackageIds(archives.Keys, GetWorkers(owner).Keys);
            AssertReplacementProcess(crashed, GetWorkers(owner)[ShellPackageId]);
            Assert.Empty(await installed.GetInstalledAsync());
            VerifyDatabases();
            await installed.ShutdownAsync(sessions);
            initialized = false;
            await WriteNativeScopeAsync(metrics, rid, archives, "unpublished-development-import", null);

            void VerifyDatabases()
            {
                VerifySqliteDatabase(Path.Combine(paths.PackageDataRootPath, AgentPackageId, "workspace", "agent", "agent.db"), "SchemaMigrations", projections[AgentPackageId]);
                VerifySqliteDatabase(Path.Combine(paths.PackageDataRootPath, SemanticMemoryPackageId, "workspace", "memory", "agent-memory.db"), "MemorySchemaMigrations", projections[SemanticMemoryPackageId]);
            }
        }
        finally
        {
            CaptureWorkers(owner, observed, temporary);
            try { if (initialized) await installed.ShutdownAsync(sessions); }
            finally
            {
                try { await runtimeHost.StopAsync(CancellationToken.None); }
                finally
                {
                    await DisposeRuntimeHostAsync(runtimeHost, appConnection);
                    await AssertProcessesStoppedAsync(observed);
                    Assert.All(temporary, path => Assert.False(Directory.Exists(path), "Native Worker temporary directory survived cleanup: " + path));
                    MakeTreeWritable(root);
                    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                }
            }
        }
    }

    private static async Task VerifyMcpAppJsonHighlightingAsync(string archive, string rid, string metrics)
    {
        var root = Directory.CreateTempSubdirectory("sunder-mcp-app-projection-");
        try
        {
            var physicalRoot = ResolvePhysicalDirectory(root.FullName);
            var extracted = Path.Combine(physicalRoot, "package");
            var projection = Path.Combine(physicalRoot, "app");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archive, extracted, deadline.Token);
            Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
            var key = new SunderPackageTargetKey("app", rid);
            Assert.True(SunderPackageTargetResolver.TryResolveTarget(validation.Manifest!, key, out var target));
            await PackageProjectionMaterializer.MaterializeProjectionAsync(extracted, projection, validation.Manifest!,
                validation.ContentIndex!, key, deadline.Token);

            var assembly = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sunder-agent-native-app-target-path.txt"), deadline.Token)).Trim();
            var executable = Path.Combine(Path.GetDirectoryName(assembly)!, OperatingSystem.IsWindows() ? "Sunder.App.exe" : "Sunder.App");
            var hook = Path.Combine(AppContext.BaseDirectory, "agent-native-app-hook", "Sunder.App.Tests.dll");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = physicalRoot };
            foreach (var name in start.Environment.Keys.ToArray())
            {
                if (name.StartsWith("DYLD_", StringComparison.Ordinal) || name.StartsWith("SUNDER_REGISTRY_", StringComparison.Ordinal)
                    || name is "GH_TOKEN" or "GITHUB_TOKEN" or "SUNDER_AGENT_NATIVE_REGISTRY_TOKEN"
                    or "DOTNET_ADDITIONAL_DEPS" or "DOTNET_SHARED_STORE" or "NUGET_PACKAGES" or "LD_LIBRARY_PATH" or "LD_PRELOAD")
                    start.Environment.Remove(name);
            }
            start.Environment["DOTNET_STARTUP_HOOKS"] = hook;
            start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
            start.Environment["SUNDER_HOME"] = physicalRoot;
            start.Environment["SUNDER_TEST_MCP_JSON_PROJECTION"] = ArchiveRelativePath.Parse(target!.EntryPoint!).ToPlatformPath(projection);
            var logs = Path.Combine(metrics, "apps", "mcp-json");
            await using var child = CapturedProcess.Start(start, maximumOutputCharacters: 64 * 1024, diagnosticsDirectory: logs);
            try
            {
                var result = await child.EnsureSuccessAsync(TimeSpan.FromSeconds(45), deadline.Token);
                Assert.Contains("MCP JSON highlighting passed", result.StandardOutput, StringComparison.Ordinal);
            }
            finally { await File.WriteAllTextAsync(Path.Combine(logs, "process.txt"), child.Diagnostics); }
        }
        finally { root.Delete(recursive: true); }
    }

    private static async Task WriteNativeScopeAsync(string metrics, string rid, IReadOnlyDictionary<string, string> archives, string profile, string? registryOrigin)
    {
        var folder = Path.Combine(metrics, "scope");
        Directory.CreateDirectory(folder);
        var inputs = archives.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
        {
            packageId = pair.Key, archive = Path.GetFileName(pair.Value), size = new FileInfo(pair.Value).Length,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(pair.Value))),
        }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(folder, "qualification.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, rid, profile, installedAcceptance = profile == "published-installed", registryOrigin, archives = inputs,
        }));
    }
}
