using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Rpc;
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
        if (TestsPackage(McpPackageId)) await VerifyMcpAppJsonHighlightingAsync(archives[McpPackageId], rid, metrics);
        var root = Path.Combine(ResolvePhysicalDirectory(Path.GetTempPath()), "sunder-native-development-" + Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(Path.Combine(root, "runtime"));
        RuntimeLocalState.EnsureInitialized(paths.RootPath);
        var runtimeHost = CreateNativeRuntimeHost(paths, out var diagnostics, out var appConnection);
        var services = runtimeHost.Services;
        var owner = services.GetRequiredService<RuntimeSessionOwner>();
        var sessions = services.GetRequiredService<PackageSessionLifecycleService>();
        var installed = services.GetRequiredService<InstalledPackageLifecycleService>();
        var observed = new HashSet<ExecutionRuntimeProcessObservation>();
        var temporary = new HashSet<string>(StringComparer.Ordinal);
        var initialized = false;
        await RunWithNativeCleanupAsync(async () =>
        {
            await StartNativeRuntimeHostAsync(runtimeHost, appConnection);
            await installed.InitializeAsync();
            initialized = true;
            var receipt = await ProvisionWorkerRuntimeAsync(services);
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
            VerifyActiveFamily(services, rid);
            Assert.All(owner.State.ActiveSession.LoadedPackageMap.Values, package =>
            {
                Assert.Equal(PackageSourceKind.Dev, package.Source.Kind);
                Assert.True(package.Source.DevelopmentImports!.IsLocal);
                CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(package, receipt, services);
            });
            // Observe the staged replacement before activation, preserving the
            // real process-start-to-activation timing boundary used by the budget.
            var initial = CaptureWorkers(owner, observed, temporary);
            var ui = services.GetRequiredService<RuntimePackageUiService>();
            var originalSnapshot = TestsPackage(AgentPackageId)
                ? Assert.Single(ui.GetActiveSnapshots(rid), item => item.PackageId == AgentPackageId) : null;
            var snapshotPath = Path.Combine(root, "agent.snapshot");
            if (originalSnapshot is not null)
            {
                using var lease = ui.AcquireCurrent(originalSnapshot.SnapshotId);
                Assert.NotNull(lease);
                await using (var destination = new FileStream(snapshotPath, FileMode.CreateNew))
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    await lease.Stream.CopyToAsync(destination, deadline.Token);
                }
                Assert.Equal(originalSnapshot.ContentHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(snapshotPath))));
            }
            var stage = await sessions.StageAsync(new PackageLifecycleStageRequest(archives.Keys.ToArray()));
            Assert.True(stage.Success, string.Join(Environment.NewLine, stage.Errors));
            var staged = GetWorkers(Assert.IsType<ActivePackageSession>(sessions.GetStagedSession(stage.StageId!)));
            AssertSamePackageIds(archives.Keys, staged.Keys);
            var oldWorkers = GetWorkers(owner);
            Assert.All(staged, pair =>
            {
                Assert.NotSame(oldWorkers[pair.Key], pair.Value);
                Assert.False(pair.Value.ActivationCompletion.IsCompleted);
            });
            var measurements = staged.ToDictionary(pair => pair.Key, pair => CaptureActivationMeasurement(pair.Value));
            var reload = await sessions.CommitStageAsync(stage.StageId!);
            CaptureWorkers(owner, observed, temporary);
            Assert.True(reload.Success, string.Join(Environment.NewLine, reload.Errors));
            VerifyActiveFamily(services, rid);
            Assert.All(staged, pair => Assert.Same(pair.Value, GetWorkers(owner)[pair.Key]));
            await AssertWorkersStoppedAsync(initial);
            await RecordWorkerMetricsAsync(GetWorkers(owner), projections, archives, rid, measurements, metrics, observed, temporary);
            VerifyDatabases();
            owner.MarkReady();
            if (originalSnapshot is not null)
            {
                await VerifySupervisedSnapshotAsync(services, paths, appConnection, originalSnapshot, snapshotPath, rid, metrics, root);
                await VerifyOwnerUnaryAndStreamAsync(services, owner, rid, appConnection, root);
            }
            if (TestsPackage(McpPackageId)) await VerifyMcpCallbackAsync(services, owner);
            if (TestsPackage(OpenAiPackageId)) await VerifyOpenAiAuthAsync(services);
            ExecutionRuntimeProcessObservation? crashed = TestsPackage(ShellPackageId) ? await VerifyCrashIsolationAsync(owner, observed, temporary) : null;
            var beforeUnload = CaptureWorkers(owner, observed, temporary);
            var unload = await sessions.LoadStartupDevPackagesAsync([]);
            Assert.True(unload.Success, string.Join(Environment.NewLine, unload.Errors));
            Assert.Empty(GetWorkers(owner));
            await AssertWorkersStoppedAsync(beforeUnload);
            var recovered = await sessions.LoadStartupDevPackagesAsync(folders.Values.ToArray());
            CaptureWorkers(owner, observed, temporary);
            Assert.True(recovered.Success, string.Join(Environment.NewLine, recovered.Errors));
            AssertSamePackageIds(archives.Keys, GetWorkers(owner).Keys);
            VerifyActiveFamily(services, rid);
            if (crashed is { } retired) AssertReplacementProcess(retired, GetWorkers(owner)[ShellPackageId]);
            Assert.Empty(await installed.GetInstalledAsync());
            VerifyDatabases();
            await installed.ShutdownAsync(sessions);
            initialized = false;
            await WriteNativeScopeAsync(metrics, rid, archives, "unpublished-development-import", null);

            void VerifyDatabases()
            {
                if (TestsPackage(AgentPackageId))
                    VerifySqliteDatabase(Path.Combine(paths.PackageDataRootPath, AgentPackageId, "workspace", "agent", "agent.db"), "SchemaMigrations", projections[AgentPackageId]);
                if (TestsPackage(SemanticMemoryPackageId))
                    VerifySqliteDatabase(Path.Combine(paths.PackageDataRootPath, SemanticMemoryPackageId, "workspace", "memory", "agent-memory.db"), "MemorySchemaMigrations", projections[SemanticMemoryPackageId]);
            }
        }, async () =>
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
                    DeleteNativeFixtureRoot(root);
                }
            }
        });
    }

    private static void VerifyActiveFamily(IServiceProvider services, string rid)
    {
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            RequiredMakerPath("SUNDER_AGENT_REPOSITORY_ROOT", directory: true), "compatibility/agent-current-inventory.json")));
        var packages = inventory.RootElement.GetProperty("packages").EnumerateArray()
            .Where(row => row.GetProperty("packageId").GetString() is { } id && (TestsPackage(id) || id == AgentPackageId)).ToArray();
        AssertSamePackageIds(packages.Where(row => row.GetProperty("hasApp").GetBoolean()).Select(row => row.GetProperty("packageId").GetString()!),
            services.GetRequiredService<RuntimePackageUiService>().GetActiveSnapshots(rid).Select(item => item.PackageId));
        var expected = packages.SelectMany(row => row.GetProperty("providers").EnumerateObject().Select(provider =>
            (PackageId: row.GetProperty("packageId").GetString()!, ProviderId: provider.Name, ContractId: provider.Value.GetString()!)));
        var active = services.GetRequiredService<RuntimeRpcCatalog>().GetSnapshot().Providers;
        Assert.All(active, item => Assert.Equal(SunderRpcProviderState.Active, item.State));
        Assert.Equal(expected.Order(),
            active.Select(item => (item.PackageId, item.ProviderId, item.ContractId)).Order());
    }

    private static async Task VerifySupervisedSnapshotAsync(IServiceProvider services, RuntimePackagePaths paths,
        RuntimeConnectionInfo connection, PackageUiSnapshotDescriptor original, string snapshotPath, string rid, string metrics, string root)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var snapshot = Assert.Single(services.GetRequiredService<RuntimePackageUiService>().GetActiveSnapshots(rid),
            row => row.PackageId == AgentPackageId);
        Assert.Equal(original.ContentHash, snapshot.ContentHash);
        Assert.Equal(original.SourceContentIdentity, snapshot.SourceContentIdentity);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(original.Target), JsonSerializer.SerializeToElement(snapshot.Target)));
        var package = Assert.Single(services.GetRequiredService<PackageSessionLifecycleService>().GetActivePackages(), row => row.PackageId == AgentPackageId);
        var connectionPath = Path.Combine(root, "snapshot-connection.json");
        RuntimeConnectionInfoStore.Save(connection, connectionPath);
        var config = Path.Combine(root, "snapshot-config.json");
        await File.WriteAllBytesAsync(config, JsonSerializer.SerializeToUtf8Bytes(new
        {
            connectionPath, package, snapshot, snapshotPath,
            sessionRoot = Path.Combine(root, "app-session"),
            runtimeRoot = paths.RootPath,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), deadline.Token);
        var assembly = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sunder-agent-native-app-target-path.txt"), deadline.Token)).Trim();
        var start = CreateNativeAppStartInfo(assembly, root);
        start.RedirectStandardInput = true;
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("SUNDER_", StringComparison.OrdinalIgnoreCase)
            || key is "GH_TOKEN" or "GITHUB_TOKEN" || key.StartsWith("DOTNET_STARTUP_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["DOTNET_STARTUP_HOOKS"] = Path.Combine(AppContext.BaseDirectory, "agent-native-app-hook", "Sunder.App.Tests.dll");
        start.Environment["SUNDER_TEST_AGENT_FAMILY_APP_CONFIG"] = config;
        start.Environment["SUNDER_APP_STATE_ROOT"] = Path.Combine(root, "snapshot-app-state");
        start.Environment[RuntimeLocalState.StateRootEnvironmentVariable] = paths.RootPath;
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        var logs = Path.Combine(metrics, "apps", "agent-snapshot");
        await using var child = CapturedProcess.Start(start, 256 * 1024, logs);
        try
        {
            await child.WaitForOutputAsync(output => output.Contains("agent-family-app: materialized-and-activated", StringComparison.Ordinal),
                TimeSpan.FromSeconds(90), deadline.Token);
            var policyRoot = Path.Combine(paths.RootPath, RuntimeV1StateDescriptor.AppPolicySessionsDirectory);
            using (var proof = JsonDocument.Parse(await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(policyRoot, "*.json")), deadline.Token)))
            {
                Assert.Equal(child.Id, proof.RootElement.GetProperty("process").GetProperty("processId").GetInt32());
                Assert.Equal(child.Id, proof.RootElement.GetProperty("peer").GetProperty("processId").GetInt32());
                var loaded = Assert.Single(proof.RootElement.GetProperty("loaded").EnumerateArray());
                Assert.Equal(AgentPackageId, loaded.GetProperty("package").GetProperty("packageId").GetString());
                Assert.True(loaded.GetProperty("development").GetBoolean());
                Assert.True(!loaded.TryGetProperty("registryOrigin", out var origin) || origin.ValueKind == JsonValueKind.Null);
                Assert.NotEmpty(proof.RootElement.GetProperty("activations").EnumerateArray());
            }
            child.Process.StandardInput.Close();
            var result = await child.EnsureSuccessAsync(TimeSpan.FromSeconds(45), deadline.Token);
            Assert.Contains("agent-family-app: retired", result.StandardOutput, StringComparison.Ordinal);
            await services.GetRequiredService<AppProcessPolicySessions>().EnforceAsync(deadline.Token);
            Assert.Empty(Directory.GetFiles(policyRoot, "*.json"));
            Assert.Empty(await services.GetRequiredService<InstalledPackageLifecycleService>().GetInstalledAsync(deadline.Token));
        }
        finally { await File.WriteAllTextAsync(Path.Combine(logs, "process.txt"), child.Diagnostics); }
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
            var hook = Path.Combine(AppContext.BaseDirectory, "agent-native-app-hook", "Sunder.App.Tests.dll");
            var start = CreateNativeAppStartInfo(assembly, physicalRoot);
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
