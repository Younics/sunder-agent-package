using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sunder.Package.Format;
using Sunder.Package.Projections;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed class AgentNativeReleaseSmokeTests(
    OfficialRuntimeFixture executionRuntimes)
    : IClassFixture<OfficialRuntimeFixture>
{
    private const string AgentPackageId = "younics.ai.agent";
    private const string BuilderPackageId = "younics.sunder.package.maker";
    private const string McpPackageId = "younics.ai.agent.mcp";
    private const string OpenAiPackageId = "younics.ai.agent.provider.openai";
    private const string SemanticMemoryPackageId = "younics.ai.agent.memory.semantic";
    private const string ShellPackageId = "younics.ai.agent.tools.shell";
    private const string AgentOwnerContractId = "younics.ai.agent.owner.runtime";
    private const string BuilderRuntimeContractId = "younics.sunder.package.maker.runtime";

    [NativeReleaseFact]
    [Trait("Category", "Release")]
    [Trait("Capability", "Native")]
    [Trait("Size", "Large")]
    public async Task CurrentRidArchives_ExerciseEveryNativeWorkerThroughRuntimeHost()
    {
        var archiveDirectory = Environment.GetEnvironmentVariable("SUNDER_AGENT_NATIVE_ARCHIVE_DIR")
            ?? throw new InvalidOperationException(
                "A selected release lane is missing a required prerequisite. "
                + "Set SUNDER_AGENT_NATIVE_ARCHIVE_DIR to run the native release smoke test.");

        archiveDirectory = Path.GetFullPath(archiveDirectory);
        var metricsDirectory = Path.GetFullPath(
            Environment.GetEnvironmentVariable("SUNDER_AGENT_NATIVE_METRICS_DIR")
            ?? throw new InvalidOperationException("SUNDER_AGENT_NATIVE_METRICS_DIR is required."));
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        var archives = ReadArchives(archiveDirectory, rid);
        Assert.Equal(15, archives.Count);

        var root = Path.Combine(
            ResolvePhysicalDirectory(Path.GetTempPath()),
            "sunder-agent-native-release",
            Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(Path.Combine(root, "runtime"));
        RuntimeLocalState.EnsureInitialized(paths.RootPath);
        var runtimeReceipt = await executionRuntimes.EnsureDotNetAsync("10.0.12");
        var launchResolver = new CapturingExecutionRuntimeLaunchResolver(executionRuntimes.LaunchResolver);
        var services = new ServiceCollection();
        services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("native-release-smoke-token"));
        services.AddSingleton(executionRuntimes.Layout);
        services.AddSingleton(executionRuntimes.Store);
        services.AddSingleton(executionRuntimes.LocalBindingResolver);
        services.AddSingleton<IEphemeralExecutionRuntimeBindingResolver>(executionRuntimes.LocalBindingResolver);
        services.AddSingleton<IExecutionRuntimeLaunchResolver>(launchResolver);
        services.AddSingleton<IExecutionRuntimeRetentionManager>(executionRuntimes.Store);
        services.AddSingleton(new RuntimeLifecyclePolicyOptions
        {
            PackageWorkerStartupTimeout = TimeSpan.FromMinutes(8),
            PackageRuntimeGenerationActivationTimeout = TimeSpan.FromMinutes(2),
        });
        services.AddSingleton(new RuntimeProcessPolicyOptions
        {
            StartupTimeout = TimeSpan.FromSeconds(30),
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
            Assert.True(
                install.Success,
                string.Join(Environment.NewLine, install.Errors.Concat(stagedWorkers.Select(worker =>
                    $"{worker.Key}: {DescribeWorkerFault(worker.Value)}{Environment.NewLine}"
                    + launchResolver.DescribeStandardError(worker.Key)))));

            var installed = await installedLifecycle.GetInstalledAsync();
            Assert.Equal(15, installed.Count);
            var installedRecords = await provider.GetRequiredService<InstalledPackageStore>().ListAsync();
            Assert.Equal(15, installedRecords.Count);
            Assert.All(installedRecords, package => CurrentRidRuntimeHostTestSupport.AssertBindingMatches(
                Assert.Single(package.ExecutionRuntimeBindings),
                runtimeReceipt));
            var initialWorkers = GetWorkers(sessionOwner);
            Assert.Equal(15, initialWorkers.Count);
            Assert.All(
                sessionOwner.State.ActiveSession.LoadedPackageMap,
                package => CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(
                    package.Value,
                    runtimeReceipt,
                    executionRuntimes));
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
            await VerifyBuilderProjectLifecycleAsync(
                provider,
                sessionOwner,
                rid,
                root,
                executionRuntimes.GetRuntimeRoot(runtimeReceipt));
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
            MakeTreeWritable(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Assert.False(Directory.Exists(root));
        }
    }

    private static void MakeTreeWritable(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        if (OperatingSystem.IsWindows()) return;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
            Assert.Same(activation.Worker, worker);
            var processStartedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var activatedAtUtc = await activation.ActivatedAtUtc;
            var startup = activatedAtUtc - processStartedAtUtc;
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
        var activatedAtUtc = worker.ActivationCompletion.ContinueWith(
            static completion =>
            {
                completion.GetAwaiter().GetResult();
                return DateTimeOffset.UtcNow;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return new WorkerActivationMeasurement(worker, activatedAtUtc);
    }

    private static string DescribeWorkerFault(ProcessRuntimeWorker worker)
    {
        var fault = typeof(ProcessRuntimeWorker)
            .GetField("_fault", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(worker) as Exception;
        return fault?.ToString() ?? "no Worker fault was recorded";
    }

    private sealed class CapturingExecutionRuntimeLaunchResolver(IExecutionRuntimeLaunchResolver inner)
        : IExecutionRuntimeLaunchResolver
    {
        private readonly ConcurrentDictionary<string, BoundedTextCapture> _standardError =
            new(StringComparer.Ordinal);

        public async Task<ResolvedExecutionRuntimeLaunch> ResolveAsync(
            PreparedRuntimePackage package,
            Guid activationId,
            CancellationToken cancellationToken)
        {
            var resolved = await inner.ResolveAsync(package, activationId, cancellationToken);
            var lease = resolved.TransferLease();
            try
            {
                return new ResolvedExecutionRuntimeLaunch(
                    resolved.RuntimeRootPath,
                    resolved.LauncherPath,
                    resolved.ArgumentTokens,
                    new CapturingExecutionRuntimeProcessLease(
                        lease,
                        _standardError.GetOrAdd(package.PackageId, static _ => new BoundedTextCapture())),
                    resolved.Identity,
                    resolved.ActivationWarning,
                    resolved.CorrelationId);
            }
            catch
            {
                await lease.DisposeAsync();
                throw;
            }
        }

        public string DescribeStandardError(string packageId)
            => _standardError.TryGetValue(packageId, out var capture)
                ? $"stderr: {capture.Read()}"
                : "stderr: <launch was not resolved>";
    }

    private sealed class CapturingExecutionRuntimeProcessLease(
        IExecutionRuntimeProcessLease inner,
        BoundedTextCapture standardError) : IExecutionRuntimeProcessLease
    {
        public string LeaseId => inner.LeaseId;
        public string RuntimeRootPath => inner.RuntimeRootPath;

        public async Task<IExecutionRuntimeContainedProcess> LaunchAsync(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken = default,
            PackageProjectionAcquisition? packageProjection = null)
            => new CapturingExecutionRuntimeContainedProcess(
                await inner.LaunchAsync(startInfo, cancellationToken, packageProjection),
                standardError);

        public Task ReleaseAfterProcessTreeTerminationAsync(CancellationToken cancellationToken = default)
            => inner.ReleaseAfterProcessTreeTerminationAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class CapturingExecutionRuntimeContainedProcess : IExecutionRuntimeContainedProcess
    {
        private readonly IExecutionRuntimeContainedProcess _inner;
        private readonly Stream _standardError;

        public CapturingExecutionRuntimeContainedProcess(
            IExecutionRuntimeContainedProcess inner,
            BoundedTextCapture standardError)
        {
            _inner = inner;
            _standardError = new CapturingReadStream(inner.StandardError, standardError);
        }

        public Process Process => _inner.Process;
        public Stream StandardInput => _inner.StandardInput;
        public Stream StandardOutput => _inner.StandardOutput;
        public Stream StandardError => _standardError;

        public Task TerminateAndDrainAsync(CancellationToken cancellationToken = default)
            => _inner.TerminateAndDrainAsync(cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class CapturingReadStream(Stream inner, BoundedTextCapture capture) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            capture.Append(buffer.AsSpan(offset, read));
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            capture.Append(buffer.AsSpan(offset, read));
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            capture.Append(buffer.Span[..read]);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class BoundedTextCapture
    {
        private const int MaximumBytes = 16 * 1024;
        private readonly byte[] _buffer = new byte[MaximumBytes];
        private int _length;
        private bool _truncated;

        public void Append(ReadOnlySpan<byte> bytes)
        {
            lock (_buffer)
            {
                var copyLength = Math.Min(bytes.Length, MaximumBytes - _length);
                bytes[..copyLength].CopyTo(_buffer.AsSpan(_length));
                _length += copyLength;
                _truncated |= copyLength < bytes.Length;
            }
        }

        public string Read()
        {
            lock (_buffer)
            {
                if (_length == 0) return "<empty>";
                var text = Encoding.UTF8.GetString(_buffer, 0, _length).Trim();
                return _truncated ? text + " [truncated]" : text;
            }
        }
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
        var sessions = services.GetRequiredService<RuntimeRpcAppSessionManager>();
        var broker = services.GetRequiredService<RuntimeRpcBroker>();
        var appSession = await OpenAppSessionAsync(sessions, owner, AgentPackageId, rid);
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
                    description = "Exercises the owner stream over an official-runtime Worker.",
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

    private static async Task VerifyBuilderProjectLifecycleAsync(
        IServiceProvider services,
        RuntimeSessionOwner owner,
        string rid,
        string testRoot,
        string workerRuntimeRoot)
    {
        var selectedSdkVersion = Environment.GetEnvironmentVariable("SUNDER_AGENT_BUILDER_SDK_VERSION");
        Assert.False(
            string.IsNullOrWhiteSpace(selectedSdkVersion),
            "SUNDER_AGENT_BUILDER_SDK_VERSION must identify the exact project-build SDK.");
        Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+$", selectedSdkVersion!);

        var selectedDotnetPath = Environment.GetEnvironmentVariable("SUNDER_AGENT_BUILDER_DOTNET_PATH");
        Assert.False(
            string.IsNullOrWhiteSpace(selectedDotnetPath),
            "SUNDER_AGENT_BUILDER_DOTNET_PATH must identify the selected project-build toolchain.");
        selectedDotnetPath = Path.GetFullPath(selectedDotnetPath!);
        Assert.True(File.Exists(selectedDotnetPath), $"Selected dotnet executable was not found: {selectedDotnetPath}");
        Assert.False(
            IsSameOrChildPath(selectedDotnetPath, workerRuntimeRoot),
            "The Builder project SDK must remain separate from the shared Worker execution-runtime store.");
        var selectedToolchainDirectory = Path.GetDirectoryName(selectedDotnetPath);
        Assert.False(string.IsNullOrWhiteSpace(selectedToolchainDirectory));

        var workspaceRoot = Path.Combine(testRoot, "builder-workspace");
        var projectRoot = Path.Combine(workspaceRoot, "NativeBuilderSmoke");
        var publishRoot = Path.Combine(workspaceRoot, "published");
        Directory.CreateDirectory(workspaceRoot);
        await File.WriteAllTextAsync(
            Path.Combine(workspaceRoot, "global.json"),
            JsonSerializer.Serialize(new
            {
                sdk = new
                {
                    version = selectedSdkVersion,
                    rollForward = "disable",
                    allowPrerelease = false,
                },
            }),
            CancellationToken.None);

        var sessions = services.GetRequiredService<RuntimeRpcAppSessionManager>();
        var broker = services.GetRequiredService<RuntimeRpcBroker>();
        var launchDiagnostics = Assert.IsType<CapturingExecutionRuntimeLaunchResolver>(
            services.GetRequiredService<IExecutionRuntimeLaunchResolver>());
        var agentAppSession = await OpenAppSessionAsync(sessions, owner, AgentPackageId, rid);
        string workspaceId;
        try
        {
            var catalog = await broker.DiscoverAppAsync(
                agentAppSession.SessionId,
                AgentOwnerContractId,
                CancellationToken.None);
            var endpoint = Assert.Single(catalog.Providers, provider =>
                provider.PackageId == AgentPackageId && provider.ProviderId == "agent.owner.runtime").Endpoint;
            var created = await broker.InvokeAppAsync(
                agentAppSession.SessionId,
                endpoint,
                "agent-runtime",
                "workspace-command",
                JsonSerializer.SerializeToElement(new
                {
                    kind = 0,
                    displayName = "Native Builder smoke",
                }),
                options: null,
                CancellationToken.None);
            workspaceId = created.GetProperty("workspace").GetProperty("workspaceId").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(workspaceId));

            var now = DateTimeOffset.UtcNow.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
                CultureInfo.InvariantCulture);
            JsonElement saved;
            try
            {
                saved = await broker.InvokeAppAsync(
                    agentAppSession.SessionId,
                    endpoint,
                    "agent-runtime",
                    "workspace-command",
                    JsonSerializer.SerializeToElement(new
                    {
                        kind = 1,
                        workspaceId,
                        displayName = "Native Builder smoke",
                        description = "Exercises Builder through the packaged shared Runtime boundary.",
                        paths = new[]
                        {
                            new
                            {
                                pathId = Guid.NewGuid().ToString("N"),
                                workspaceId,
                                hostPath = workspaceRoot,
                                isDefault = true,
                                sortOrder = 0,
                                createdAtUtc = now,
                                updatedAtUtc = now,
                            },
                        },
                        documents = Array.Empty<object>(),
                        executionTargetId = "local",
                    }),
                    options: null,
                    CancellationToken.None);
            }
            catch (SunderRpcException exception)
            {
                throw new InvalidOperationException(
                    $"Agent workspace save failed with RPC error '{exception.Error.Code}': "
                    + $"{exception.Error.Message} Details: {exception.Error.Details?.GetRawText() ?? "<none>"}. "
                    + launchDiagnostics.DescribeStandardError(AgentPackageId),
                    exception);
            }
            var savedWorkspace = saved.GetProperty("workspace");
            Assert.Equal(workspaceId, savedWorkspace.GetProperty("workspaceId").GetString());
            Assert.Equal(
                workspaceRoot,
                Path.GetFullPath(Assert.Single(savedWorkspace.GetProperty("paths").EnumerateArray())
                    .GetProperty("hostPath")
                    .GetString()!));
        }
        finally
        {
            sessions.Close(agentAppSession.SessionId);
        }

        var builderAppSession = await OpenAppSessionAsync(sessions, owner, BuilderPackageId, rid);
        try
        {
            var workspaces = await InvokeBuilderAsync(
                broker,
                builderAppSession.SessionId,
                launchDiagnostics,
                JsonSerializer.SerializeToElement(new { kind = "list-workspaces", timeoutSeconds = 600 }));
            Assert.Contains(
                workspaces.GetProperty("workspaces").EnumerateArray(),
                workspace => workspace.GetProperty("workspaceId").GetString() == workspaceId);

            var execution = (await InvokeBuilderAsync(
                    broker,
                    builderAppSession.SessionId,
                    launchDiagnostics,
                    JsonSerializer.SerializeToElement(new
                    {
                        kind = "resolve-workspace",
                        workspaceId,
                        timeoutSeconds = 600,
                    })))
                .GetProperty("execution");
            Assert.Equal("local", execution.GetProperty("target").GetProperty("targetId").GetString());
            Assert.True(execution.GetProperty("supportsPathEnvironment").GetBoolean());
            Assert.Contains(
                execution.GetProperty("scope").GetProperty("workspacePaths").EnumerateArray(),
                path => string.Equals(
                    Path.GetFullPath(path.GetString()!),
                    workspaceRoot,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

            _ = await InvokeBuilderAsync(
                broker,
                builderAppSession.SessionId,
                launchDiagnostics,
                JsonSerializer.SerializeToElement(new
                {
                    kind = "add-path-entry",
                    workspaceId,
                    timeoutSeconds = 600,
                    executionPath = selectedToolchainDirectory,
                }));

            var version = AssertBuilderProcessSucceeded(
                await InvokeBuilderProcessAsync(
                    broker,
                    builderAppSession.SessionId,
                    launchDiagnostics,
                    workspaceId,
                    ["--version"],
                    workspaceRoot,
                    120),
                "SDK selection");
            Assert.Equal(selectedSdkVersion, version.GetProperty("standardOutput").GetString()!.Trim());

            AssertBuilderProcessSucceeded(
                await InvokeBuilderProcessAsync(
                    broker,
                    builderAppSession.SessionId,
                    launchDiagnostics,
                    workspaceId,
                    [
                        "new", "console",
                        "--name", "NativeBuilderSmoke",
                        "--framework", "net10.0",
                        "--output", projectRoot,
                        "--no-restore",
                    ],
                    workspaceRoot,
                    300),
                "project creation");
            Assert.True(File.Exists(Path.Combine(projectRoot, "NativeBuilderSmoke.csproj")));

            AssertBuilderProcessSucceeded(
                await InvokeBuilderProcessAsync(
                    broker,
                    builderAppSession.SessionId,
                    launchDiagnostics,
                    workspaceId,
                    ["build", projectRoot, "--configuration", "Release", "--nologo"],
                    projectRoot,
                    600),
                "project build");

            AssertBuilderProcessSucceeded(
                await InvokeBuilderProcessAsync(
                    broker,
                    builderAppSession.SessionId,
                    launchDiagnostics,
                    workspaceId,
                    [
                        "publish", projectRoot,
                        "--configuration", "Release",
                        "--no-restore",
                        "--output", publishRoot,
                        "--nologo",
                    ],
                    projectRoot,
                    900),
                "project publish");
            Assert.True(File.Exists(Path.Combine(publishRoot, "NativeBuilderSmoke.dll")));
        }
        finally
        {
            sessions.Close(builderAppSession.SessionId);
        }
    }

    private static async Task<JsonElement> InvokeBuilderProcessAsync(
        RuntimeRpcBroker broker,
        string appSessionId,
        CapturingExecutionRuntimeLaunchResolver launchDiagnostics,
        string workspaceId,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int timeoutSeconds)
        => await InvokeBuilderAsync(
            broker,
            appSessionId,
            launchDiagnostics,
            JsonSerializer.SerializeToElement(new
            {
                kind = "execute-process",
                workspaceId,
                fileName = "dotnet",
                arguments,
                workingDirectory,
                timeoutSeconds,
            }));

    private static JsonElement AssertBuilderProcessSucceeded(JsonElement response, string operation)
    {
        var process = response.GetProperty("process");
        var output = process.GetProperty("standardOutput").GetString() ?? string.Empty;
        var error = process.GetProperty("standardError").GetString() ?? string.Empty;
        Assert.True(
            process.GetProperty("exitCode").GetInt32() == 0,
            $"Builder {operation} failed:{Environment.NewLine}{output}{Environment.NewLine}{error}");
        Assert.False(process.GetProperty("wasTruncated").GetBoolean());
        return process;
    }

    private static async Task<JsonElement> InvokeBuilderAsync(
        RuntimeRpcBroker broker,
        string appSessionId,
        CapturingExecutionRuntimeLaunchResolver launchDiagnostics,
        JsonElement request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        await using var scope = new RuntimeRpcCallScope(
            broker,
            broker.CreateAppCallScope(
                appSessionId,
                new SunderRpcCallOptions(DateTimeOffset.UtcNow.AddMinutes(20)),
                timeout.Token));
        var catalog = await scope.DiscoverAsync(BuilderRuntimeContractId, timeout.Token);
        var endpoint = Assert.Single(catalog.Providers, provider =>
            provider.PackageId == BuilderPackageId && provider.ProviderId == "builder.runtime").Endpoint;
        JsonElement response;
        try
        {
            response = await scope.InvokeAsync(
                endpoint,
                "builder-runtime",
                "execute",
                request,
                options: null,
                timeout.Token);
        }
        catch (SunderRpcException exception)
        {
            var operation = request.GetProperty("kind").GetString();
            throw new InvalidOperationException(
                $"Builder operation '{operation}' failed with RPC error '{exception.Error.Code}': "
                + $"{exception.Error.Message} Details: {exception.Error.Details?.GetRawText() ?? "<none>"}. "
                + launchDiagnostics.DescribeStandardError(BuilderPackageId) + "; "
                + launchDiagnostics.DescribeStandardError(AgentPackageId) + "; "
                + launchDiagnostics.DescribeStandardError("younics.ai.agent.execution.local"),
                exception);
        }
        var reference = ReadContentReference(response.GetProperty("content"));
        Assert.Equal("application/json", reference.MediaType);
        Assert.Equal("builder-result.json", reference.FileName);
        Assert.Equal(SunderRpcContentRepeatability.SingleUse, reference.Repeatability);
        Assert.True(reference.ExpiresAtUtc <= scope.DeadlineUtc);

        await using var content = await scope.OpenContentAsync(reference, timeout.Token);
        using var buffer = new MemoryStream(checked((int)reference.Length));
        await content.CopyToAsync(buffer, timeout.Token);
        var bytes = buffer.ToArray();
        Assert.Equal(reference.Length, bytes.LongLength);
        Assert.Equal(
            reference.Sha256,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    private static SunderRpcContentReference ReadContentReference(JsonElement value)
        => new(
            value.GetProperty("id").GetString()!,
            value.GetProperty("length").GetInt64(),
            value.GetProperty("sha256").GetString()!,
            value.GetProperty("mediaType").GetString()!,
            value.GetProperty("fileName").GetString()!,
            value.GetProperty("expiresAtUtc").GetDateTimeOffset(),
            value.GetProperty("repeatability").GetString() == "single-use"
                ? SunderRpcContentRepeatability.SingleUse
                : SunderRpcContentRepeatability.Repeatable);

    private static async Task<RuntimeRpcAppSessionDescriptor> OpenAppSessionAsync(
        RuntimeRpcAppSessionManager sessions,
        RuntimeSessionOwner owner,
        string packageId,
        string rid)
    {
        Assert.True(owner.State.ActiveSession.TryGetPackageSource(packageId, out var source));
        Assert.NotNull(source?.Manifest);
        Assert.NotNull(source.ContentIndex);
        var appTarget = Assert.Single(source.Manifest.Targets!, target =>
            target is not null
            && target.Role == SunderPackageFormat.AppHostRole
            && target.Rid == rid)!;
        return await sessions.OpenAsync(
            new RuntimeRpcAppSessionOpenRequest(
                packageId,
                source.Manifest.Version!,
                RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex),
                PackageTargetSelection.ToDescriptor(appTarget),
                owner.Generation,
                Guid.NewGuid()),
            CancellationToken.None);
    }

    private static bool IsSameOrChildPath(string candidatePath, string rootPath)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(rootPath), Path.GetFullPath(candidatePath));
        return relative == "."
               || (relative != ".."
                   && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !Path.IsPathFullyQualified(relative));
    }

    private static string ResolvePhysicalDirectory(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"Directory path has no root: {fullPath}");
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, fullPath).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }
        return current;
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
        ProcessRuntimeWorker Worker,
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

            var busyTimeout = LoadDelegate<SqliteBusyTimeoutDelegate>(_libraryHandle, "sqlite3_busy_timeout");
            result = busyTimeout(_databaseHandle, 15_000);
            if (result != SqliteOk)
            {
                var message = ReadErrorMessage();
                _close(_databaseHandle);
                _databaseHandle = 0;
                NativeLibrary.Free(_libraryHandle);
                throw new InvalidOperationException(
                    $"SQLite could not configure the release database busy timeout: {message} ({result}).");
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
        private delegate int SqliteBusyTimeoutDelegate(nint database, int milliseconds);

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

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class NativeReleaseFactAttribute : FactAttribute
{
    public NativeReleaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("SUNDER_AGENT_NATIVE_ARCHIVE_DIR"))
            && !string.Equals(
                Environment.GetEnvironmentVariable("SUNDER_REQUIRE_TEST_PREREQUISITES"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set SUNDER_AGENT_NATIVE_ARCHIVE_DIR to run the native release smoke test.";
        }
    }
}
