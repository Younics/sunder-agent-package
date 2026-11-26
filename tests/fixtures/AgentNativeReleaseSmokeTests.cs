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
using Sunder.Registry.Contracts;
using Microsoft.AspNetCore.Builder;
using Sunder.Runtime.Host.Endpoints;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed partial class AgentNativeReleaseSmokeTests
{
    private const string AgentPackageId = "younics.ai.agent";
    private const string BuilderPackageId = "younics.sunder.package.maker";
    private const string McpPackageId = "younics.ai.agent.mcp";
    private const string OpenAiPackageId = "younics.ai.agent.provider.openai";
    private const string SemanticMemoryPackageId = "younics.ai.agent.memory.semantic";
    private const string ShellPackageId = "younics.ai.agent.tools.shell";
    private const string AgentOwnerContractId = "younics.ai.agent.owner.runtime";
    private const string BuilderRuntimeContractId = "younics.sunder.package.maker.runtime";
    private static string WorkerRuntimeVersion => Environment.GetEnvironmentVariable("SUNDER_AGENT_TEST_WORKER_DOTNET_VERSION")
        ?? throw new InvalidOperationException("Select the pinned native Worker runtime test input.");

    [NativeReleaseFact]
    [Trait("Category", "Release")]
    [Trait("Capability", "Native")]
    [Trait("Size", "Large")]
    public async Task PublishedArchives_InstallActivateAndExerciseEveryNativeWorkerThroughRuntimeHost()
    {
        var registryOrigin = RegistryActivePolicyClient.Origin(Environment.GetEnvironmentVariable("SUNDER_AGENT_NATIVE_REGISTRY_ORIGIN")
            ?? throw new InvalidOperationException("Published native installed acceptance requires an explicit HTTPS Registry origin."));
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

        var root = Path.Combine(
            ResolvePhysicalDirectory(Path.GetTempPath()),
            "sunder-agent-native-release",
            Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(Path.Combine(root, "runtime"));
        RuntimeLocalState.EnsureInitialized(paths.RootPath);
        var runtimeHost = CreateNativeRuntimeHost(paths, out var launchResolver, out var appConnection);
        var provider = runtimeHost.Services;
        var observedProcesses = new HashSet<ExecutionRuntimeProcessObservation>();
        var observedTemporaryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var initialized = false;

        await RunWithNativeCleanupAsync(async () =>
        {
            await StartNativeRuntimeHostAsync(runtimeHost, appConnection);
            var installedLifecycle = provider.GetRequiredService<InstalledPackageLifecycleService>();
            var sessionLifecycle = provider.GetRequiredService<PackageSessionLifecycleService>();
            var sessionOwner = provider.GetRequiredService<RuntimeSessionOwner>();
            await installedLifecycle.InitializeAsync();
            initialized = true;
            var runtimeReceipt = await ProvisionWorkerRuntimeAsync(provider);

            var subjects = new List<RegistryPackageVersionIdentity>();
            var platforms = new HashSet<RegistryPlatformCatalogIdentity>();
            foreach (var (packageId, archivePath) in archives)
            {
                var extracted = Path.Combine(root, "published-subjects", packageId);
                var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archivePath, extracted);
                Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
                subjects.Add(new(packageId, validation.Manifest!.Version!, PackageSessionPreparer.ComputeContentIdentity(extracted)));
                platforms.UnionWith(PackagePolicyAdmission.Platforms(validation.Manifest));
            }
            // Genuine nonce-bound HTTPS observations, never a seeded/fabricated
            // cache or disabled admission. Unpublished/denied bytes must fail.
            await provider.GetRequiredService<RegistryPackagePolicySynchronizer>().RefreshAsync(
                [new(registryOrigin, subjects, platforms.ToArray())], CancellationToken.None);
            var policies = provider.GetRequiredService<PackageLifecyclePolicyCache>();
            foreach (var subject in subjects) policies.RequireActivation(registryOrigin, subject);

            var transfers = provider.GetRequiredService<RuntimeContentTransferStore>();
            var registry = provider.GetRequiredService<RegistryHttpClient>();
            var installMutations = new List<PackageStoreMutationRequest>(archives.Count);
            var downloads = new List<object>();
            foreach (var (packageId, archivePath) in archives.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                var subject = subjects.Single(subject => subject.PackageId == packageId);
                var length = new FileInfo(archivePath).Length;
                var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archivePath)));
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get,
                    registryOrigin + RegistryApiRoutes.PackageVersionCanonical(packageId, subject.Version));
                // Exact-version downloads, including Yanked versions, are public.
                // Exercise that boundary without a human session or publication key.
                using var response = await registry.SendAsync(request, CancellationToken.None);
                Assert.Null(request.Headers.Authorization);
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
                if (response.Content.Headers.ContentLength is { } advertised) Assert.Equal(length, advertised);
                await using var archive = await response.Content.ReadAsStreamAsync();
                var upload = await transfers.CreateUploadAsync(
                    RuntimeUploadKind.Package,
                    archive,
                    length,
                    expectedHash: hash,
                    Path.GetFileName(archivePath),
                    "application/vnd.sunder.package",
                    sessionOwner.Generation,
                    CancellationToken.None);
                installMutations.Add(new PackageStoreMutationRequest(
                    PackageStoreMutationKind.Install,
                    UploadId: upload.UploadId,
                    CapabilityConsent: ReadCapabilityConsent(archivePath)));
                downloads.Add(new { packageId, version = subject.Version, archiveSha256 = hash, size = length });
            }
            // Downloads and all-family extraction can outlive the initial lease.
            // Renew through the real HTTPS boundary immediately before admission.
            await provider.GetRequiredService<RegistryPackagePolicySynchronizer>().RefreshAsync(
                [new(registryOrigin, subjects, platforms.ToArray())], CancellationToken.None);
            var policyEvidence = subjects.Select(subject => new { package = subject, lease = policies.PackageLease(registryOrigin, subject) }).ToArray();
            var stage = await installedLifecycle.StageAsync(
                new PackageStoreStageRequest(installMutations),
                CancellationToken.None);
            Assert.True(stage.Success, string.Join(Environment.NewLine, stage.Errors));
            var stageId = Assert.IsType<string>(stage.StageId);
            var stagedSession = Assert.IsType<ActivePackageSession>(
                installedLifecycle.GetStagedSession(stageId));
            var stagedWorkers = GetWorkers(stagedSession);
            AssertSamePackageIds(archives.Keys, stagedWorkers.Keys);
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
            AssertSamePackageIds(archives.Keys, installed.Select(package => package.PackageId));
            var installedRecords = await provider.GetRequiredService<InstalledPackageStore>().ListAsync();
            AssertSamePackageIds(archives.Keys, installedRecords.Select(package => package.PackageId));
            Assert.All(installedRecords, package => CurrentRidRuntimeHostTestSupport.AssertBindingMatches(
                Assert.Single(package.ExecutionRuntimeBindings),
                runtimeReceipt));
            var initialWorkers = GetWorkers(sessionOwner);
            AssertSamePackageIds(archives.Keys, initialWorkers.Keys);
            Assert.All(
                sessionOwner.State.ActiveSession.LoadedPackageMap,
                package => CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(
                    package.Value,
                    runtimeReceipt,
                    provider));
            await RecordWorkerMetricsAsync(
                initialWorkers,
                installed.ToDictionary(package => package.PackageId, package => paths.GetInstalledPackagePath(package.PackageId, package.Version)),
                archives,
                rid,
                activationMeasurements,
                metricsDirectory,
                observedProcesses,
                observedTemporaryPaths);

            VerifyDatabases();
            sessionOwner.MarkReady();
            if (TestsPackage(AgentPackageId))
                await VerifyOwnerUnaryAndStreamAsync(provider, sessionOwner, rid, appConnection, root);
            if (TestsPackage(McpPackageId)) await VerifyMcpCallbackAsync(provider, sessionOwner);
            if (TestsPackage(OpenAiPackageId)) await VerifyOpenAiAuthAsync(provider);
            ExecutionRuntimeProcessObservation? crashedShellProcess = TestsPackage(ShellPackageId)
                ? await VerifyCrashIsolationAsync(sessionOwner, observedProcesses, observedTemporaryPaths) : null;

            var beforeDisable = CaptureWorkers(sessionOwner, observedProcesses, observedTemporaryPaths);
            var disable = await installedLifecycle.ExecutePreparedMutationsAsync(installed
                .OrderByDescending(static package => package.PackageId, StringComparer.Ordinal)
                .Select(package => new PackageStoreMutation(PackageStoreMutationKind.Disable, package.PackageId))
                .ToArray(), CancellationToken.None);
            Assert.True(disable.Success, string.Join(Environment.NewLine, disable.Errors));
            Assert.Empty(sessionOwner.State.ActiveSession.LoadedPackageMap);
            await AssertWorkersStoppedAsync(beforeDisable);

            var reinstallId = ReleasePackage ?? ShellPackageId;
            var reinstall = await installedLifecycle.UpgradeFromRuntimePathAsync(
                reinstallId,
                archives[reinstallId],
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
            AssertSamePackageIds(archives.Keys, recoveredWorkers.Keys);
            if (crashedShellProcess is { } retired) AssertReplacementProcess(retired, recoveredWorkers[ShellPackageId]);
            CaptureWorkers(sessionOwner, observedProcesses, observedTemporaryPaths);
            VerifyDatabases();

            void VerifyDatabases()
            {
                if (TestsPackage(AgentPackageId))
                    VerifySqliteDatabase(Path.Combine(paths.PackageDataRootPath, AgentPackageId, "workspace", "agent", "agent.db"),
                        "SchemaMigrations", GetInstalledPackagePath(paths, installed, AgentPackageId));
                if (TestsPackage(SemanticMemoryPackageId))
                    VerifySqliteDatabase(Path.Combine(paths.PackageDataRootPath, SemanticMemoryPackageId, "workspace", "memory", "agent-memory.db"),
                        "MemorySchemaMigrations", GetInstalledPackagePath(paths, installed, SemanticMemoryPackageId));
            }

            await installedLifecycle.ShutdownAsync(sessionLifecycle);
            initialized = false;
            await WriteNativeScopeAsync(metricsDirectory, rid, archives, "published-installed", registryOrigin);
            await File.WriteAllTextAsync(Path.Combine(metricsDirectory, "scope", "published-registry.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 2, registryOrigin, archiveDownloadAuthentication = "anonymous",
                downloads, policies = policyEvidence,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }, async () =>
        {
            try
            {
                if (initialized)
                    await provider.GetRequiredService<InstalledPackageLifecycleService>()
                        .ShutdownAsync(provider.GetRequiredService<PackageSessionLifecycleService>());
            }
            finally
            {
                try { await runtimeHost.StopAsync(CancellationToken.None); }
                finally { await DisposeRuntimeHostAsync(runtimeHost, appConnection); }
                await AssertProcessesStoppedAsync(observedProcesses);
                Assert.All(observedTemporaryPaths, path => Assert.False(
                    Directory.Exists(path),
                    $"Worker temporary directory survived Runtime Host lifecycle: {path}"));
                DeleteNativeFixtureRoot(root);
            }
        });
    }

    private static IHost CreateNativeRuntimeHost(RuntimePackagePaths paths,
        out CapturingExecutionRuntimeLaunchResolver launchResolver, out RuntimeConnectionInfo appConnection)
    {
        // Use the real Generic Host composition, including its application
        // lifetime; a bare ServiceCollection cannot run the update scheduler.
        var builder = WebApplication.CreateSlimBuilder();
        var endpoint = new RuntimeIpcEndpoint(OperatingSystem.IsWindows() ? RuntimeIpcTransportKind.NamedPipe : RuntimeIpcTransportKind.UnixSocket,
            OperatingSystem.IsWindows() ? "sunder-native-" + Guid.NewGuid().ToString("N") : Path.Combine(ResolvePhysicalDirectory(Path.GetTempPath()), "sn-" + Guid.NewGuid().ToString("N")[..12] + ".sock"));
        RuntimeSupervisedTransport.Configure(builder.WebHost, endpoint);
        var services = builder.Services;
        services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("native-release-smoke-token"));
        services.AddSingleton<ExecutionRuntimeLaunchResolver>();
        services.AddSingleton<CapturingExecutionRuntimeLaunchResolver>();
        services.AddSingleton<IExecutionRuntimeLaunchResolver>(provider => provider.GetRequiredService<CapturingExecutionRuntimeLaunchResolver>());
        services.AddSingleton(new RuntimeLifecyclePolicyOptions
        {
            PackageWorkerStartupTimeout = TimeSpan.FromMinutes(8),
            PackageRuntimeGenerationActivationTimeout = TimeSpan.FromMinutes(2),
        });
        services.AddSingleton(new RuntimeProcessPolicyOptions { StartupTimeout = TimeSpan.FromSeconds(30), ShutdownTimeout = TimeSpan.FromSeconds(15) });
        builder.Host.UseServiceProviderFactory(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
        var app = builder.Build();
        launchResolver = app.Services.GetRequiredService<CapturingExecutionRuntimeLaunchResolver>();
        app.UseMiddleware<RuntimeProblemDetailsMiddleware>();
        app.UseMiddleware<RuntimeBearerAuthenticationMiddleware>();
        app.MapRuntimeHandshakeEndpoint();
        app.MapGroup("/api/v1").MapRuntimeRpcEndpoints().MapAppPolicyEndpoints().MapContentTransferEndpoints();
        appConnection = new RuntimeConnectionInfo(RuntimeIpcEndpoint.LogicalRuntimeUrl, "native-release-smoke-token") { IpcEndpoint = endpoint };
        return app;
    }

    private static async Task<ExecutionRuntimeReceiptV1> ProvisionWorkerRuntimeAsync(IServiceProvider services)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var requirement = new RuntimeExecutionRuntimeRequirement(SunderPackageFormat.DotNetExecutionRuntimeId,
            ">=10.0.0 <11.0.0", SunderPackageFormat.DotNetWorkerModuleAbi);
        var artifact = await services.GetRequiredService<OfficialRuntimeResolver>().ResolveAsync(
            requirement, PackageTargetSelection.GetCurrentRuntimeIdentifier(), deadline.Token, WorkerRuntimeVersion);
        var installed = await services.GetRequiredService<ExecutionRuntimeArtifactDownloader>().DownloadAsync(artifact, deadline.Token);
        Assert.Equal(WorkerRuntimeVersion, installed.Receipt.Identity.VendorVersion);
        return installed.Receipt;
    }

    private static async Task StartNativeRuntimeHostAsync(IHost host, RuntimeConnectionInfo connection)
    {
        await host.StartAsync(CancellationToken.None);
        RuntimeSupervisedTransport.RestrictEndpoint(connection.IpcEndpoint!);
    }

    private static async Task DisposeRuntimeHostAsync(IHost host, RuntimeConnectionInfo connection)
    {
        try
        {
            if (host is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync();
            else host.Dispose();
        }
        finally
        {
            if (!OperatingSystem.IsWindows() && connection.IpcEndpoint is { Kind: RuntimeIpcTransportKind.UnixSocket } endpoint)
                File.Delete(endpoint.Address); // Only this fixture's freshly allocated socket.
        }
    }

    private static async Task RunWithNativeCleanupAsync(Func<Task> qualify, Func<Task> cleanup)
    {
        try
        {
            await qualify();
        }
        catch (Exception failure)
        {
            try { await cleanup(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Native qualification and cleanup both failed.", failure, cleanupFailure);
            }
            throw;
        }
        await cleanup();
    }

    private static void DeleteNativeFixtureRoot(string root)
    {
        // The installed execution runtime is sealed, including directory ACLs on
        // Windows. Reclaim only this test's owned tree after checking process exit.
        ExecutionRuntimeRootFileSystem.DeleteOwnedTree(root);
        Assert.False(Directory.Exists(root));
    }

    private static string? ReleasePackage => Environment.GetEnvironmentVariable("SUNDER_AGENT_RELEASE_PACKAGE");
    private static bool TestsPackage(string id) => ReleasePackage is null || ReleasePackage == id;

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
                .Where(target => target.GetProperty("role").GetString() == "runtime"
                    && target.GetProperty("rid").GetString() == rid)
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
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            RequiredMakerPath("SUNDER_AGENT_REPOSITORY_ROOT", directory: true), "compatibility/agent-current-inventory.json")));
        var known = inventory.RootElement.GetProperty("packages").EnumerateArray().Select(row => row.GetProperty("packageId").GetString()!).ToArray();
        if (ReleasePackage is { } subject) Assert.Contains(subject, known);
        AssertSamePackageIds(known.Where(id => TestsPackage(id) || id == AgentPackageId), result.Keys);
        return result;
    }

    private static void AssertSamePackageIds(IEnumerable<string> expected, IEnumerable<string> observed)
        => Assert.Equal(expected.Order(StringComparer.Ordinal), observed.Order(StringComparer.Ordinal));

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

    private static async Task RecordWorkerMetricsAsync(
        IReadOnlyDictionary<string, ProcessRuntimeWorker> workers,
        IReadOnlyDictionary<string, string> projections,
        IReadOnlyDictionary<string, string> archives,
        string rid,
        IReadOnlyDictionary<string, WorkerActivationMeasurement> activationMeasurements,
        string metricsDirectory,
        ISet<ExecutionRuntimeProcessObservation> observedProcesses,
        ISet<string> observedTemporaryPaths)
    {
        Directory.CreateDirectory(metricsDirectory);
        var processParents = await CaptureProcessParentsAsync();
        // Observe all sampled trees before the per-Worker metric writes, which
        // settle each RSS sample separately. Don't count an already exited PID
        // just because it was present in the parent map.
        var census = workers.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            var process = WorkerReleaseMetrics.GetProcess(pair.Value);
            var tree = GetWorkerProcessTree(process.Id, processParents)
                .Select(id => DescribeObservedProcess(id, processParents[id])).ToArray();
            return (pair.Key, pair.Value, Process: process, Tree: tree);
        }).ToArray();
        foreach (var (packageId, worker, process, processTree) in census)
        {
            var activation = activationMeasurements[packageId];
            Assert.Same(activation.Worker, worker);
            var processStartedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var activatedAtUtc = await activation.ActivatedAtUtc;
            var startup = activatedAtUtc - processStartedAtUtc;
            Assert.True(startup > TimeSpan.Zero, $"Worker '{packageId}' did not record a positive start-to-active interval.");
            observedProcesses.Add(ProcessIdentity.Observe(process));
            observedTemporaryPaths.Add(worker.WorkerTemporaryPath);
            Console.WriteLine("Native Worker process tree for " + packageId + ": " + JsonSerializer.Serialize(processTree));
            Assert.Contains(processTree, row => row.ProcessId == process.Id && row.IsAlive is true
                && row.StartedAtUtcTicks == processStartedAtUtc.Ticks);
            // An unreadable child might still be alive. Count it conservatively.
            var workerProcessCount = processTree.Count(row => row.IsAlive is not false);
            await WorkerReleaseMetrics.RecordAsync(
                "dotnetWorker",
                rid,
                archives[packageId],
                WorkerReleaseMetrics.DirectoryBytes(projections[packageId]),
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

    private sealed class CapturingExecutionRuntimeLaunchResolver(ExecutionRuntimeLaunchResolver inner)
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

    private static IReadOnlyList<int> GetWorkerProcessTree(
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
        return workerProcesses.Order().ToArray();
    }

    private static NativeProcessDiagnostic DescribeObservedProcess(int id, int parent)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(id);
        }
        catch (ArgumentException error)
        {
            // A sampled local PID no longer exists.
            return new(id, parent, false, null, null, error.GetType().Name);
        }
        using (process)
        {
            try
            {
                if (process.HasExited) return new(id, parent, false, null, null, null);
                return new(id, parent, true, process.ProcessName, process.StartTime.ToUniversalTime().Ticks, null);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new(id, parent, null, null, null, error.GetType().Name);
            }
        }
    }

    private sealed record NativeProcessDiagnostic(int ProcessId, int ParentProcessId, bool? IsAlive, string? Name,
        long? StartedAtUtcTicks, string? ObservationError);

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
        string rid, RuntimeConnectionInfo connection, string root)
    {
        await using var app = await NativeAppRpcProxy.CreateAsync(services, AgentPackageId, rid, connection, Path.Combine(root, "agent-app"));
        {
            var dashboard = await app.InvokeAsync(AgentOwnerContractId,
                "agent-runtime",
                "dashboard",
                JsonSerializer.SerializeToElement(new { }),
                content: false);
            var revision = dashboard.GetProperty("revision").GetInt64();
            // The App proxy owns the live stream and the mutation in one real
            // supervised App process, retaining the original streaming assertion.
            var mutation = await app.InvokeAsync(AgentOwnerContractId,
                "agent-runtime",
                "profile-command",
                JsonSerializer.SerializeToElement(new
                {
                    kind = 0,
                    // Nullable DTO members remain required on the reviewed wire;
                    // a hand-authored native request must not omit their nulls.
                    profileId = (string?)null,
                    displayName = "Native release smoke",
                    description = "Exercises the owner stream over an official-runtime Worker.",
                    instructions = (string?)null,
                    chatProviderId = (string?)null,
                    chatModelId = (string?)null,
                    embeddingProviderId = (string?)null,
                    embeddingModelId = (string?)null,
                    assignments = (object?)null,
                    behaviorLoopId = (string?)null,
                    behaviorLoopSourceId = (string?)null,
                    behaviorLoopSettingsJson = (string?)null,
                    chatModelSettingsJson = (string?)null,
                }),
                content: false, afterRevision: revision);
            var mutationRevision = mutation.GetProperty("revision").GetInt64();
            Assert.True(mutationRevision > revision);
            Assert.False(string.IsNullOrWhiteSpace(mutation.GetProperty("profile").GetProperty("profileId").GetString()));
            Assert.Equal("Native release smoke", mutation.GetProperty("profile").GetProperty("displayName").GetString());
        }
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

    private static async Task VerifyMcpCallbackAsync(
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

    }

    private static async Task VerifyOpenAiAuthAsync(IServiceProvider services)
    {
        var callbackAccess = services.GetRequiredService<PackageCallbackAccessService>();
        var callbackServer = services.GetRequiredService<PackageCallbackServer>();
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

    private static readonly SystemExecutionRuntimeProcessController ProcessIdentity = new();

    private static async Task<ExecutionRuntimeProcessObservation> VerifyCrashIsolationAsync(
        RuntimeSessionOwner owner,
        ISet<ExecutionRuntimeProcessObservation> observedProcesses,
        ISet<string> observedTemporaryPaths)
    {
        var worker = GetWorkers(owner)[ShellPackageId];
        var survivingPackages = GetWorkers(owner).Keys.Where(id => id != ShellPackageId).ToArray();
        var process = WorkerReleaseMetrics.GetProcess(worker);
        var oldProcess = ProcessIdentity.Observe(process);
        var oldTemporaryPath = worker.WorkerTemporaryPath;
        observedProcesses.Add(oldProcess);
        observedTemporaryPaths.Add(oldTemporaryPath);
        process.Kill();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await worker.GenerationCompletion.WaitAsync(TimeSpan.FromSeconds(15)));
        await AssertProcessesStoppedAsync([oldProcess]);
        await WaitUntilAsync(() => !Directory.Exists(oldTemporaryPath));
        await WaitUntilAsync(() => !owner.State.ActiveSession.LoadedPackageMap.ContainsKey(ShellPackageId));
        AssertSamePackageIds(survivingPackages, GetWorkers(owner).Keys);
        return oldProcess;
    }

    private static void AssertReplacementProcess(ExecutionRuntimeProcessObservation retired, ProcessRuntimeWorker replacement)
    {
        var current = ProcessIdentity.Observe(WorkerReleaseMetrics.GetProcess(replacement));
        Assert.NotEqual((retired.ProcessId, retired.BirthIdentity), (current.ProcessId, current.BirthIdentity));
    }

    private static IReadOnlyList<(ExecutionRuntimeProcessObservation Process, string TemporaryPath)> CaptureWorkers(
        RuntimeSessionOwner owner,
        ISet<ExecutionRuntimeProcessObservation> observedProcesses,
        ISet<string> observedTemporaryPaths)
    {
        var result = GetWorkers(owner).Values.Select(worker =>
        {
            var process = ProcessIdentity.Observe(WorkerReleaseMetrics.GetProcess(worker));
            observedProcesses.Add(process);
            observedTemporaryPaths.Add(worker.WorkerTemporaryPath);
            return (process, worker.WorkerTemporaryPath);
        }).ToArray();
        return result;
    }

    private static async Task AssertWorkersStoppedAsync(
        IEnumerable<(ExecutionRuntimeProcessObservation Process, string TemporaryPath)> workers)
    {
        var snapshot = workers.ToArray();
        await AssertProcessesStoppedAsync(snapshot.Select(static worker => worker.Process));
        foreach (var worker in snapshot)
        {
            await WaitUntilAsync(() => !Directory.Exists(worker.TemporaryPath));
        }
    }

    private static async Task AssertProcessesStoppedAsync(IEnumerable<ExecutionRuntimeProcessObservation> processes)
    {
        foreach (var process in processes.Distinct())
        {
            // A PID can be assigned to an unrelated process before final cleanup.
            // Missing and a different birth identity prove the original is gone;
            // attachment/observation errors are not evidence of a successful exit.
            var probe = ExecutionRuntimeProcessProbe.Unavailable;
            await WaitUntilAsync(
                () => (probe = ProcessIdentity.Probe(process.ProcessId, process.BirthIdentity, process.ExecutablePath))
                    is ExecutionRuntimeProcessProbe.Missing or ExecutionRuntimeProcessProbe.BirthMismatch,
                () => $"Native Worker PID {process.ProcessId} ({process.BirthIdentity}) did not confirm exit after retirement: {probe}");
            if (probe == ExecutionRuntimeProcessProbe.BirthMismatch)
                Console.WriteLine($"Native Worker PID {process.ProcessId} ({process.BirthIdentity}) was reused after retirement.");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, Func<string>? failure = null)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate() && timeout.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
        Assert.True(predicate(), failure?.Invoke() ?? "Timed out waiting for Runtime Host lifecycle cleanup.");
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
