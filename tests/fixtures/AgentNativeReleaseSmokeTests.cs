using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class AgentNativeReleaseSmokeTests
{
    private const string AgentPackageId = "sunder.package.agent";
    private const string McpPackageId = "sunder.package.agent.mcp";
    private const string OpenAiPackageId = "sunder.package.agent.provider.openai";
    private const string SemanticMemoryPackageId = "sunder.package.agent.memory.semantic";
    private const string ShellPackageId = "sunder.package.agent.tools.shell";
    private const string AgentOwnerContractId = "sunder.agent.owner.runtime";

    [Fact]
    public async Task CurrentRidArchives_ExerciseEveryNativeWorkerThroughRuntimeHost()
    {
        var archiveDirectory = Environment.GetEnvironmentVariable("SUNDER_AGENT_NATIVE_ARCHIVE_DIR");
        if (string.IsNullOrWhiteSpace(archiveDirectory)) return;

        archiveDirectory = Path.GetFullPath(archiveDirectory);
        var metricsDirectory = Path.GetFullPath(
            Environment.GetEnvironmentVariable("SUNDER_AGENT_NATIVE_METRICS_DIR")
            ?? throw new InvalidOperationException("SUNDER_AGENT_NATIVE_METRICS_DIR is required."));
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        var archives = ReadArchives(archiveDirectory, rid);
        Assert.Equal(15, archives.Count);

        var root = Path.Combine(Path.GetTempPath(), "sunder-agent-native-release", Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(Path.Combine(root, "runtime"));
        var services = new ServiceCollection();
        services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("native-release-smoke-token"));
        services.AddSingleton(new RuntimeLifecyclePolicyOptions
        {
            PackageWorkerStartupTimeout = TimeSpan.FromMinutes(8),
            PackageRuntimeGenerationActivationTimeout = TimeSpan.FromMinutes(2),
        });
        services.AddSingleton(new RuntimeProcessPolicyOptions
        {
            StartupTimeout = TimeSpan.FromSeconds(30),
            ActivationTimeout = TimeSpan.FromSeconds(30),
            ShutdownTimeout = TimeSpan.FromSeconds(15),
        });
        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var hostedServices = provider.GetServices<IHostedService>()
            .Distinct<IHostedService>(ReferenceEqualityComparer.Instance)
            .ToArray();
        var observedProcessIds = new HashSet<int>();
        var observedTemporaryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var initialized = false;

        try
        {
            foreach (var hostedService in hostedServices) await hostedService.StartAsync(CancellationToken.None);
            var installedLifecycle = provider.GetRequiredService<InstalledPackageLifecycleService>();
            var sessionLifecycle = provider.GetRequiredService<PackageSessionLifecycleService>();
            var sessionOwner = provider.GetRequiredService<RuntimeSessionOwner>();
            await installedLifecycle.InitializeAsync();
            initialized = true;

            var transfers = provider.GetRequiredService<RuntimeContentTransferStore>();
            var installMutations = new List<PackageStoreMutationRequest>(archives.Count);
            foreach (var archivePath in archives.Values.OrderBy(static path => path, StringComparer.Ordinal))
            {
                await using var archive = File.OpenRead(archivePath);
                var upload = await transfers.CreateUploadAsync(
                    RuntimeUploadKind.Package,
                    archive,
                    archive.Length,
                    expectedHash: null,
                    Path.GetFileName(archivePath),
                    "application/vnd.sunder.package",
                    sessionOwner.Generation,
                    CancellationToken.None);
                installMutations.Add(new PackageStoreMutationRequest(
                    PackageStoreMutationKind.Install,
                    UploadId: upload.UploadId,
                    CapabilityConsent: ReadCapabilityConsent(archivePath)));
            }
            var stage = await installedLifecycle.StageAsync(
                new PackageStoreStageRequest(installMutations),
                CancellationToken.None);
            Assert.True(stage.Success, string.Join(Environment.NewLine, stage.Errors));
            var stageId = Assert.IsType<string>(stage.StageId);
            var stagedSession = Assert.IsType<ActivePackageSession>(
                installedLifecycle.GetStagedSession(stageId));
            var stagedWorkers = GetWorkers(stagedSession);
            Assert.Equal(15, stagedWorkers.Count);
            var activationMeasurements = stagedWorkers.ToDictionary(
                static pair => pair.Key,
                static pair => CaptureActivationMeasurement(pair.Value),
                StringComparer.Ordinal);

            var install = await installedLifecycle.CommitStageAsync(stageId, CancellationToken.None);
            Assert.True(install.Success, string.Join(Environment.NewLine, install.Errors));

            var installed = await installedLifecycle.GetInstalledAsync();
            Assert.Equal(15, installed.Count);
            var initialWorkers = GetWorkers(sessionOwner);
            Assert.Equal(15, initialWorkers.Count);
            await RecordMetricsAndMigrationsAsync(
                initialWorkers,
                installed,
                archives,
                paths,
                rid,
                activationMeasurements,
                metricsDirectory,
                observedProcessIds,
                observedTemporaryPaths);

            VerifySqliteDatabase(
                Path.Combine(paths.PackageDataRootPath, AgentPackageId, "workspace", "agent", "agent.db"),
                "SchemaMigrations",
                GetInstalledPackagePath(paths, installed, AgentPackageId));
            VerifySqliteDatabase(
                Path.Combine(paths.PackageDataRootPath, SemanticMemoryPackageId, "workspace", "memory", "agent-memory.db"),
                "MemorySchemaMigrations",
                GetInstalledPackagePath(paths, installed, SemanticMemoryPackageId));
            await VerifyOwnerUnaryAndStreamAsync(provider, sessionOwner, rid);
            await VerifyCallbackAndAuthAsync(provider, sessionOwner);
            var crashedShellProcessId = await VerifyCrashIsolationAsync(
                sessionOwner,
                observedProcessIds,
                observedTemporaryPaths);

            var beforeDisable = CaptureWorkers(sessionOwner, observedProcessIds, observedTemporaryPaths);
            var disable = await installedLifecycle.ExecutePreparedMutationsAsync(installed
                .OrderByDescending(static package => package.PackageId, StringComparer.Ordinal)
                .Select(package => new PackageStoreMutation(PackageStoreMutationKind.Disable, package.PackageId))
                .ToArray(), CancellationToken.None);
            Assert.True(disable.Success, string.Join(Environment.NewLine, disable.Errors));
            Assert.Empty(sessionOwner.State.ActiveSession.LoadedPackageMap);
            await AssertWorkersStoppedAsync(beforeDisable);

            var reinstall = await installedLifecycle.UpgradeFromRuntimePathAsync(
                ShellPackageId,
                archives[ShellPackageId],
                reinstall: true,
                cancellationToken: CancellationToken.None);
            Assert.True(reinstall.Success, string.Join(Environment.NewLine, reinstall.Errors));
            Assert.Empty(sessionOwner.State.ActiveSession.LoadedPackageMap);

            var enable = await installedLifecycle.ExecutePreparedMutationsAsync(installed
                .OrderBy(static package => package.PackageId, StringComparer.Ordinal)
                .Select(package => new PackageStoreMutation(PackageStoreMutationKind.Enable, package.PackageId))
                .ToArray(), CancellationToken.None);
            Assert.True(enable.Success, string.Join(Environment.NewLine, enable.Errors));
            var recoveredWorkers = GetWorkers(sessionOwner);
            Assert.Equal(15, recoveredWorkers.Count);
            Assert.NotEqual(
                crashedShellProcessId,
                WorkerReleaseMetrics.GetProcess(recoveredWorkers[ShellPackageId]).Id);
            CaptureWorkers(sessionOwner, observedProcessIds, observedTemporaryPaths);
            VerifySqliteDatabase(
                Path.Combine(paths.PackageDataRootPath, AgentPackageId, "workspace", "agent", "agent.db"),
                "SchemaMigrations",
                GetInstalledPackagePath(paths, installed, AgentPackageId));
            VerifySqliteDatabase(
                Path.Combine(paths.PackageDataRootPath, SemanticMemoryPackageId, "workspace", "memory", "agent-memory.db"),
                "MemorySchemaMigrations",
                GetInstalledPackagePath(paths, installed, SemanticMemoryPackageId));

            await installedLifecycle.ShutdownAsync(sessionLifecycle);
            initialized = false;
        }
        finally
        {
            if (initialized)
            {
                try
                {
                    await provider.GetRequiredService<InstalledPackageLifecycleService>()
                        .ShutdownAsync(provider.GetRequiredService<PackageSessionLifecycleService>());
                }
                catch
                {
                    // Preserve the primary test failure; process and directory assertions below still run.
                }
            }
            for (var index = hostedServices.Length - 1; index >= 0; index--)
            {
                try
                {
                    await hostedServices[index].StopAsync(CancellationToken.None);
                }
                catch
                {
                    // Provider disposal and orphan assertions remain mandatory.
                }
            }
            await provider.DisposeAsync();
            await AssertProcessIdsStoppedAsync(observedProcessIds);
            Assert.All(observedTemporaryPaths, path => Assert.False(
                Directory.Exists(path),
                $"Worker temporary directory survived Runtime Host lifecycle: {path}"));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Assert.False(Directory.Exists(root));
        }
    }

    private static Dictionary<string, string> ReadArchives(string archiveDirectory, string rid)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var archivePath in Directory.GetFiles(archiveDirectory, "*.sunderpkg").Order(StringComparer.Ordinal))
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var manifestEntry = archive.GetEntry("manifest/sunder-package.json");
            Assert.NotNull(manifestEntry);
            using var manifestStream = manifestEntry.Open();
            using var manifest = JsonDocument.Parse(manifestStream);
            var packageId = manifest.RootElement.GetProperty("id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(packageId));
            var runtimeTargets = manifest.RootElement.GetProperty("targets").EnumerateArray()
                .Where(static target => target.GetProperty("role").GetString() == "runtime")
                .ToArray();
            var target = Assert.Single(runtimeTargets);
            Assert.Equal(rid, target.GetProperty("rid").GetString());
            Assert.Equal("worker", target.GetProperty("kind").GetString());
            var appTargets = manifest.RootElement.GetProperty("targets").EnumerateArray()
                .Where(static candidate => candidate.GetProperty("role").GetString() == "app")
                .ToArray();
            if (appTargets.Length > 0)
            {
                Assert.Contains(appTargets, candidate => candidate.GetProperty("rid").GetString() == rid);
            }
            Assert.True(result.TryAdd(packageId!, archivePath), $"Duplicate native archive for '{packageId}'.");
        }
        return result;
    }

    private static PackageCapabilityConsent? ReadCapabilityConsent(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        using var manifestStream = archive.GetEntry("manifest/sunder-package.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var capabilities = manifest.RootElement.GetProperty("targets").EnumerateArray()
            .SelectMany(static target => target.GetProperty("requiredHostCapabilities").EnumerateArray())
            .Select(static capability => capability.GetString())
            .Where(static capability => capability is not null && PackageSensitiveCapabilityPolicy.IsSensitive(capability))
            .Select(static capability => capability!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (capabilities.Length == 0) return null;

        using var indexStream = archive.GetEntry("manifest/content-index.json")!.Open();
        using var index = JsonDocument.Parse(indexStream);
        var manifestSha256 = Assert.Single(index.RootElement.GetProperty("files").EnumerateArray(), static file =>
            file.GetProperty("path").GetString() == "manifest/sunder-package.json")
            .GetProperty("sha256")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(manifestSha256));
        return new PackageCapabilityConsent(
            manifest.RootElement.GetProperty("id").GetString()!,
            manifest.RootElement.GetProperty("version").GetString()!,
            manifestSha256!,
            capabilities);
    }

    private static Dictionary<string, ProcessRuntimeWorker> GetWorkers(RuntimeSessionOwner owner)
        => GetWorkers(owner.State.ActiveSession);

    private static Dictionary<string, ProcessRuntimeWorker> GetWorkers(ActivePackageSession session)
        => session.LoadedPackageMap.ToDictionary(
            static pair => pair.Key,
            static pair => Assert.IsType<ProcessRuntimeWorker>(pair.Value.Worker),
            StringComparer.Ordinal);

    private static async Task RecordMetricsAndMigrationsAsync(
        IReadOnlyDictionary<string, ProcessRuntimeWorker> workers,
        IReadOnlyList<InstalledPackageDescriptor> installed,
        IReadOnlyDictionary<string, string> archives,
        RuntimePackagePaths paths,
        string rid,
        IReadOnlyDictionary<string, WorkerActivationMeasurement> activationMeasurements,
        string metricsDirectory,
        ISet<int> observedProcessIds,
        ISet<string> observedTemporaryPaths)
    {
        Directory.CreateDirectory(metricsDirectory);
        var processParents = await CaptureProcessParentsAsync();
        foreach (var (packageId, worker) in workers.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var process = WorkerReleaseMetrics.GetProcess(worker);
            var activation = activationMeasurements[packageId];
            Assert.Same(activation.Process, process);
            var activatedAtUtc = await activation.ActivatedAtUtc;
            var startup = activatedAtUtc - activation.ProcessStartedAtUtc;
            Assert.True(startup > TimeSpan.Zero, $"Worker '{packageId}' did not record a positive start-to-active interval.");
            observedProcessIds.Add(process.Id);
            observedTemporaryPaths.Add(worker.WorkerTemporaryPath);
            var package = Assert.Single(installed, candidate => candidate.PackageId == packageId);
            var workerProcessCount = CountWorkerProcessTree(process.Id, processParents);
            await WorkerReleaseMetrics.RecordAsync(
                "dotnetWorker",
                rid,
                archives[packageId],
                WorkerReleaseMetrics.DirectoryBytes(paths.GetInstalledPackagePath(packageId, package.Version)),
                startup,
                workerProcessCount,
                process,
                Path.Combine(metricsDirectory, $"{packageId}.{rid}.json"));
        }
    }

    private static WorkerActivationMeasurement CaptureActivationMeasurement(ProcessRuntimeWorker worker)
    {
        var process = WorkerReleaseMetrics.GetProcess(worker);
        var processStartedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        var activatedAtUtc = worker.ActivationCompletion.ContinueWith(
            static completion =>
            {
                completion.GetAwaiter().GetResult();
                return DateTimeOffset.UtcNow;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return new WorkerActivationMeasurement(process, processStartedAtUtc, activatedAtUtc);
    }

    private static async Task<IReadOnlyDictionary<int, int>> CaptureProcessParentsAsync()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "pwsh" : "ps",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "Get-CimInstance Win32_Process | ForEach-Object { '{0} {1}' -f $_.ProcessId, $_.ParentProcessId }");
        }
        else
        {
            startInfo.ArgumentList.Add("-A");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("pid=,ppid=");
        }

        using var snapshot = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the process-tree snapshot command.");
        var output = snapshot.StandardOutput.ReadToEndAsync();
        var error = snapshot.StandardError.ReadToEndAsync();
        await snapshot.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var standardOutput = await output;
        var standardError = await error;
        Assert.True(
            snapshot.ExitCode == 0,
            $"Process-tree snapshot failed with exit code {snapshot.ExitCode}: {standardError}");

        var parentByProcess = new Dictionary<int, int>();
        foreach (var line in standardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length >= 2
                && int.TryParse(fields[0], out var processId)
                && int.TryParse(fields[1], out var parentProcessId))
            {
                parentByProcess[processId] = parentProcessId;
            }
        }
        return parentByProcess;
    }

    private static int CountWorkerProcessTree(
        int workerProcessId,
        IReadOnlyDictionary<int, int> parentByProcess)
    {
        Assert.Contains(workerProcessId, parentByProcess.Keys);

        var workerProcesses = new HashSet<int> { workerProcessId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (processId, parentProcessId) in parentByProcess)
            {
                if (workerProcesses.Contains(parentProcessId) && workerProcesses.Add(processId))
                {
                    changed = true;
                }
            }
        }
        return workerProcesses.Count;
    }

    private static string GetInstalledPackagePath(
        RuntimePackagePaths paths,
        IReadOnlyList<InstalledPackageDescriptor> installed,
        string packageId)
    {
        var package = Assert.Single(installed, candidate => candidate.PackageId == packageId);
        return paths.GetInstalledPackagePath(packageId, package.Version);
    }

    private static void VerifySqliteDatabase(
        string databasePath,
        string ledgerTable,
        string installedPackagePath)
    {
        Assert.True(File.Exists(databasePath), $"Native worker did not create SQLite database: {databasePath}");
        using var database = NativeSqliteDatabase.Open(installedPackagePath, databasePath);
        Assert.Equal(1L, database.ExecuteInt64($"SELECT COUNT(*) FROM {ledgerTable};"));
        Assert.Equal(
            0L,
            database.ExecuteInt64(
                $"SELECT COUNT(*) FROM {ledgerTable} WHERE Checksum IS NULL OR length(Checksum) != 64;"));
    }

    private static async Task VerifyOwnerUnaryAndStreamAsync(
        IServiceProvider services,
        RuntimeSessionOwner owner,
        string rid)
    {
        Assert.True(owner.State.ActiveSession.TryGetPackageSource(AgentPackageId, out var source));
        Assert.NotNull(source?.Manifest);
        Assert.NotNull(source.ContentIndex);
        var appTarget = Assert.Single(source.Manifest.Targets!, target =>
            target is not null
            && target.Role == SunderPackageFormat.AppHostRole
            && target.Rid == rid)!;
        var sessions = services.GetRequiredService<RuntimeRpcAppSessionManager>();
        var broker = services.GetRequiredService<RuntimeRpcBroker>();
        var appSession = await sessions.OpenAsync(
            new RuntimeRpcAppSessionOpenRequest(
                AgentPackageId,
                source.Manifest.Version!,
                RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex),
                PackageTargetSelection.ToDescriptor(appTarget),
                owner.Generation,
                Guid.NewGuid()),
            CancellationToken.None);
        try
        {
            var catalog = await broker.DiscoverAppAsync(appSession.SessionId, AgentOwnerContractId, CancellationToken.None);
            var endpoint = Assert.Single(catalog.Providers, provider =>
                provider.PackageId == AgentPackageId && provider.ProviderId == "agent.owner.runtime").Endpoint;
            var dashboard = await broker.InvokeAppAsync(
                appSession.SessionId,
                endpoint,
                "agent-runtime",
                "dashboard",
                JsonSerializer.SerializeToElement(new { }),
                options: null,
                CancellationToken.None);
            var revision = dashboard.GetProperty("revision").GetInt64();

            using var streamTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var stream = broker.SubscribeAppAsync(
                    appSession.SessionId,
                    endpoint,
                    "agent-runtime",
                    "changes",
                    JsonSerializer.SerializeToElement(new { afterRevision = revision, supportsTurnMutations = true }),
                    options: null,
                    streamTimeout.Token)
                .GetAsyncEnumerator(streamTimeout.Token);
            Assert.True(await stream.MoveNextAsync());
            Assert.True(stream.Current.GetProperty("revision").GetInt64() >= revision);
            var mutation = await broker.InvokeAppAsync(
                appSession.SessionId,
                endpoint,
                "agent-runtime",
                "profile-command",
                JsonSerializer.SerializeToElement(new
                {
                    kind = 0,
                    displayName = "Native release smoke",
                    description = "Exercises the owner stream over a signed native worker.",
                }),
                options: null,
                streamTimeout.Token);
            var mutationRevision = mutation.GetProperty("revision").GetInt64();
            Assert.True(mutationRevision > revision);
            do
            {
                Assert.True(await stream.MoveNextAsync());
            }
            while (stream.Current.GetProperty("revision").GetInt64() < mutationRevision);
        }
        finally
        {
            sessions.Close(appSession.SessionId);
        }
    }

    private static async Task VerifyCallbackAndAuthAsync(
        IServiceProvider services,
        RuntimeSessionOwner owner)
    {
        var callbackAccess = services.GetRequiredService<PackageCallbackAccessService>();
        var callbackServer = services.GetRequiredService<PackageCallbackServer>();
        var callbackFailure = await Assert.ThrowsAnyAsync<Exception>(() => callbackAccess.StartAsync(
            McpPackageId,
            "mcp.oauth.v1",
            new PackageCallbackSessionStartRequest(),
            callbackServer));
        Assert.Equal(
            "worker.contribution-fault",
            Assert.IsType<ProcessWorkerContributionException>(callbackFailure).Code);
        Assert.Equal(PackageReadinessState.Ready, owner.State.GetSessionPackage(McpPackageId)!.Readiness);

        var auth = services.GetRequiredService<PackageAuthAccessService>();
        var authStatus = await auth.GetStatusAsync(OpenAiPackageId);
        Assert.NotNull(authStatus);
        Assert.Contains(authStatus.Status, new[] { PackageAuthStatusKind.NotConnected, PackageAuthStatusKind.Connected });
        var started = await auth.StartAsync(OpenAiPackageId, callbackServer);
        Assert.NotNull(started);
        Assert.Equal(PackageAuthFlowKind.Browser, started.Flow);
        Assert.True(Uri.TryCreate(started.LaunchUrl, UriKind.Absolute, out _));
        Assert.True(callbackAccess.Cancel(OpenAiPackageId, started.AuthSessionId));
        Assert.Equal(PackageAuthSessionState.Cancelled, auth.GetSessionStatus(OpenAiPackageId, started.AuthSessionId)!.State);
    }

    private static async Task<int> VerifyCrashIsolationAsync(
        RuntimeSessionOwner owner,
        ISet<int> observedProcessIds,
        ISet<string> observedTemporaryPaths)
    {
        var worker = GetWorkers(owner)[ShellPackageId];
        var process = WorkerReleaseMetrics.GetProcess(worker);
        var oldProcessId = process.Id;
        var oldTemporaryPath = worker.WorkerTemporaryPath;
        observedProcessIds.Add(oldProcessId);
        observedTemporaryPaths.Add(oldTemporaryPath);
        process.Kill();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(15)));
        await AssertProcessIdsStoppedAsync([oldProcessId]);
        await WaitUntilAsync(() => !Directory.Exists(oldTemporaryPath));
        await WaitUntilAsync(() => !owner.State.ActiveSession.LoadedPackageMap.ContainsKey(ShellPackageId));
        Assert.Equal(14, GetWorkers(owner).Count);
        return oldProcessId;
    }

    private static IReadOnlyList<(int ProcessId, string TemporaryPath)> CaptureWorkers(
        RuntimeSessionOwner owner,
        ISet<int> observedProcessIds,
        ISet<string> observedTemporaryPaths)
    {
        var result = GetWorkers(owner).Values.Select(worker =>
        {
            var processId = WorkerReleaseMetrics.GetProcess(worker).Id;
            observedProcessIds.Add(processId);
            observedTemporaryPaths.Add(worker.WorkerTemporaryPath);
            return (processId, worker.WorkerTemporaryPath);
        }).ToArray();
        return result;
    }

    private static async Task AssertWorkersStoppedAsync(
        IEnumerable<(int ProcessId, string TemporaryPath)> workers)
    {
        var snapshot = workers.ToArray();
        await AssertProcessIdsStoppedAsync(snapshot.Select(static worker => worker.ProcessId));
        foreach (var worker in snapshot)
        {
            await WaitUntilAsync(() => !Directory.Exists(worker.TemporaryPath));
        }
    }

    private static async Task AssertProcessIdsStoppedAsync(IEnumerable<int> processIds)
    {
        foreach (var processId in processIds.Distinct())
        {
            await WaitUntilAsync(() => !IsProcessAlive(processId));
            Assert.False(IsProcessAlive(processId), $"Native release worker process {processId} survived retirement.");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate() && timeout.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
        Assert.True(predicate(), "Timed out waiting for Runtime Host lifecycle cleanup.");
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed record WorkerActivationMeasurement(
        Process Process,
        DateTimeOffset ProcessStartedAtUtc,
        Task<DateTimeOffset> ActivatedAtUtc);

    private sealed class NativeSqliteDatabase : IDisposable
    {
        private const int SqliteOk = 0;
        private const int SqliteOpenReadOnly = 1;
        private const int SqliteRow = 100;

        private readonly nint _libraryHandle;
        private readonly SqliteCloseDelegate _close;
        private readonly SqliteColumnInt64Delegate _columnInt64;
        private readonly SqliteErrorMessageDelegate _errorMessage;
        private readonly SqliteFinalizeDelegate _finalize;
        private readonly SqlitePrepareDelegate _prepare;
        private readonly SqliteStepDelegate _step;
        private nint _databaseHandle;

        private NativeSqliteDatabase(string libraryPath, string databasePath)
        {
            _libraryHandle = NativeLibrary.Load(libraryPath);
            _close = LoadDelegate<SqliteCloseDelegate>(_libraryHandle, "sqlite3_close_v2");
            _columnInt64 = LoadDelegate<SqliteColumnInt64Delegate>(_libraryHandle, "sqlite3_column_int64");
            _errorMessage = LoadDelegate<SqliteErrorMessageDelegate>(_libraryHandle, "sqlite3_errmsg");
            _finalize = LoadDelegate<SqliteFinalizeDelegate>(_libraryHandle, "sqlite3_finalize");
            var open = LoadDelegate<SqliteOpenDelegate>(_libraryHandle, "sqlite3_open_v2");
            _prepare = LoadDelegate<SqlitePrepareDelegate>(_libraryHandle, "sqlite3_prepare_v2");
            _step = LoadDelegate<SqliteStepDelegate>(_libraryHandle, "sqlite3_step");

            var result = open(databasePath, out _databaseHandle, SqliteOpenReadOnly, 0);
            if (result != SqliteOk)
            {
                var message = ReadErrorMessage();
                if (_databaseHandle != 0) _close(_databaseHandle);
                NativeLibrary.Free(_libraryHandle);
                throw new InvalidOperationException($"SQLite could not open '{databasePath}': {message} ({result}).");
            }
        }

        public static NativeSqliteDatabase Open(string installedPackagePath, string databasePath)
        {
            var fileName = OperatingSystem.IsWindows()
                ? "e_sqlite3.dll"
                : OperatingSystem.IsMacOS()
                    ? "libe_sqlite3.dylib"
                    : "libe_sqlite3.so";
            var candidates = Directory.EnumerateFiles(installedPackagePath, fileName, SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.NotEmpty(candidates);

            var failures = new List<Exception>();
            foreach (var candidate in candidates)
            {
                try
                {
                    return new NativeSqliteDatabase(candidate, databasePath);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
            throw new AggregateException(
                $"No packaged SQLite library could query '{databasePath}' for the current architecture.",
                failures);
        }

        public long ExecuteInt64(string commandText)
        {
            var result = _prepare(_databaseHandle, commandText, -1, out var statement, out _);
            ThrowIfError(result, "prepare query");
            try
            {
                result = _step(statement);
                if (result != SqliteRow)
                {
                    throw new InvalidOperationException(
                        $"SQLite could not read a row for '{commandText}': {ReadErrorMessage()} ({result}).");
                }
                return _columnInt64(statement, 0);
            }
            finally
            {
                _finalize(statement);
            }
        }

        public void Dispose()
        {
            if (_databaseHandle == 0) return;
            var result = _close(_databaseHandle);
            _databaseHandle = 0;
            NativeLibrary.Free(_libraryHandle);
            if (result != SqliteOk)
            {
                throw new InvalidOperationException($"SQLite could not close the release database ({result}).");
            }
        }

        private string ReadErrorMessage()
            => _databaseHandle == 0
                ? "unknown error"
                : Marshal.PtrToStringUTF8(_errorMessage(_databaseHandle)) ?? "unknown error";

        private void ThrowIfError(int result, string operation)
        {
            if (result != SqliteOk)
            {
                throw new InvalidOperationException(
                    $"SQLite could not {operation}: {ReadErrorMessage()} ({result}).");
            }
        }

        private static TDelegate LoadDelegate<TDelegate>(nint libraryHandle, string exportName)
            where TDelegate : Delegate
            => Marshal.GetDelegateForFunctionPointer<TDelegate>(NativeLibrary.GetExport(libraryHandle, exportName));

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SqliteOpenDelegate(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string fileName,
            out nint database,
            int flags,
            nint virtualFileSystem);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SqliteCloseDelegate(nint database);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SqlitePrepareDelegate(
            nint database,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
            int byteCount,
            out nint statement,
            out nint tail);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SqliteStepDelegate(nint statement);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate long SqliteColumnInt64Delegate(nint statement, int column);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SqliteFinalizeDelegate(nint statement);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint SqliteErrorMessageDelegate(nint database);
    }

}
