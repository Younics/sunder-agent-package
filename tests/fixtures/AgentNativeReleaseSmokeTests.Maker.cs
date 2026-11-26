using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class AgentNativeReleaseSmokeTests
{
    // A distinct native acceptance boundary: the final Maker archive, without
    // Agent, actually creates/authors/builds/packs and activates a Sunder package
    // through the supported unpublished development-import channel.
    [Fact]
    public async Task MakerArchive_CurrentRidCreatesBuildsPacksAndInvokesWithoutAgent()
    {
        var directory = RequiredMakerPath("SUNDER_AGENT_NATIVE_ARCHIVE_DIR", directory: true);
        var metrics = RequiredMakerPath("SUNDER_AGENT_NATIVE_METRICS_DIR", directory: true);
        var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
        var archives = ReadArchives(directory, rid);
        var archive = archives[BuilderPackageId];
        var root = Path.Combine(ResolvePhysicalDirectory(Path.GetTempPath()), "sunder-native-maker-" + Guid.NewGuid().ToString("N"));
        var paths = new RuntimePackagePaths(root);
        RuntimeLocalState.EnsureInitialized(paths.RootPath);
        var runtimeHost = CreateNativeRuntimeHost(paths, out var resolver, out var appConnection);
        var provider = runtimeHost.Services;
        var owner = provider.GetRequiredService<RuntimeSessionOwner>();
        var lifecycle = provider.GetRequiredService<InstalledPackageLifecycleService>();
        var observed = new HashSet<ExecutionRuntimeProcessObservation>();
        var temporary = new HashSet<string>(StringComparer.Ordinal);
        var initialized = false;
        await RunWithNativeCleanupAsync(async () =>
        {
            await StartNativeRuntimeHostAsync(runtimeHost, appConnection);
            await lifecycle.InitializeAsync();
            initialized = true;
            var receipt = await ProvisionWorkerRuntimeAsync(provider);
            var folder = Path.Combine(root, "maker-source");
            await PrepareNativeDevelopmentPackageAsync(provider, archive, folder, archives.Values.ToArray());
            var load = await provider.GetRequiredService<PackageSessionLifecycleService>().LoadStartupDevPackagesAsync([folder]);
            CaptureWorkers(owner, observed, temporary);
            Assert.True(load.Success, string.Join(Environment.NewLine, load.Errors) + resolver.DescribeStandardError(BuilderPackageId));
            Assert.Equal(BuilderPackageId, Assert.Single(GetWorkers(owner)).Key);
            Assert.Empty(await lifecycle.GetInstalledAsync());
            owner.MarkReady();
            await VerifyStandaloneMakerAsync(provider, owner, rid, root);
            await VerifyBuilderProjectLifecycleAsync(provider, owner, rid, root,
                provider.GetRequiredService<ExecutionRuntimeStoreLayout>().GetContentRootPath(receipt.ContentIdentity), metrics, observed, temporary, appConnection);
        }, async () =>
        {
            CaptureWorkers(owner, observed, temporary);
            try
            {
                if (initialized) await lifecycle.ShutdownAsync(provider.GetRequiredService<PackageSessionLifecycleService>());
            }
            finally
            {
                try
                {
                    await runtimeHost.StopAsync(CancellationToken.None);
                }
                finally
                {
                    await DisposeRuntimeHostAsync(runtimeHost, appConnection);
                    await AssertProcessesStoppedAsync(observed);
                    Assert.All(temporary, path => Assert.False(Directory.Exists(path), "Maker Worker temporary directory survived cleanup: " + path));
                    DeleteNativeFixtureRoot(root);
                }
            }
        });
    }

    private static async Task VerifyStandaloneMakerAsync(IServiceProvider services, RuntimeSessionOwner owner, string rid, string root)
    {
        var package = Assert.Single(services.GetRequiredService<PackageSessionLifecycleService>().GetSessionPackages());
        Assert.Equal(BuilderPackageId, package.PackageId);
        Assert.True(package.IsEnabled);
        Assert.Equal(PackageReadinessState.Ready, package.Readiness);
        Assert.Equal(BuilderPackageId, Assert.Single(services.GetRequiredService<RuntimePackageUiService>().GetActiveSnapshots(rid)).PackageId);
        var catalog = services.GetRequiredService<RuntimeRpcCatalog>();
        var providers = catalog.GetSnapshot().Providers;
        Assert.Equal(new[] { "builder.runtime", "maker.permissions", "maker.tools" }, providers.Select(row => row.ProviderId).Order(StringComparer.Ordinal));
        Assert.All(providers, row =>
        {
            Assert.Equal(BuilderPackageId, row.PackageId);
            Assert.Equal(SunderRpcProviderState.Active, row.State);
        });
        var activation = owner.State.ActiveSession.LoadedPackageMap[BuilderPackageId];
        Assert.True(activation.Source.DevelopmentImports!.IsLocal);
        Assert.Null(activation.Source.DevelopmentImports.RegistryOrigin);
        var rpc = new RuntimeRpcClient(services.GetRequiredService<RuntimeRpcBroker>(), new(BuilderPackageId, activation.RuntimeActivationId));
        var denied = await Assert.ThrowsAsync<SunderRpcException>(() => rpc.DiscoverAsync("younics.ai.agent.tool.source").AsTask());
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, denied.Error.Kind);
        var tools = Assert.Single(providers, row => row.ProviderId == "maker.tools");
        var handler = Assert.Single(activation.RpcProviders.Values, row => row.ProviderId == tools.ProviderId).Handler;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var invocation = new SunderRpcInvocationContext(BuilderPackageId, package.Version, tools, DateTimeOffset.UtcNow.AddSeconds(5), 1, deadline.Token);
        var description = await handler.InvokeUnaryAsync(invocation, "tool-source", "describe", JsonSerializer.SerializeToElement(new { }), deadline.Token);
        Assert.Equal("package-maker", description.GetProperty("sourceId").GetString());
        var rejection = await handler.InvokeUnaryAsync(invocation, "tool-source", "validate-arguments", JsonSerializer.SerializeToElement(new
        {
            toolId = "maker_package",
            argumentsJson = JsonSerializer.Serialize(new { package = Path.Combine(root, "project/app/index.html") }),
        }), deadline.Token);
        Assert.True(rejection.GetProperty("isError").GetBoolean());
        Assert.Equal("maker.invalid-request", rejection.GetProperty("errorCode").GetString());
        Assert.Equal(tools.Endpoint, Assert.Single(catalog.GetSnapshot().Providers, row => row.ProviderId == tools.ProviderId).Endpoint);
        Assert.False(activation.Worker.GenerationCompletion.IsCompleted);
    }

    private static async Task VerifyBuilderProjectLifecycleAsync(IServiceProvider services, RuntimeSessionOwner owner,
        string rid, string testRoot, string workerRuntimeRoot, string metricsDirectory,
        HashSet<ExecutionRuntimeProcessObservation> observedProcesses, HashSet<string> observedTemporaryPaths, RuntimeConnectionInfo appConnection)
    {
        var sdk = Environment.GetEnvironmentVariable("SUNDER_AGENT_BUILDER_SDK_VERSION");
        Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+$", sdk ?? "");
        var executable = new FileInfo(RequiredMakerPath("SUNDER_AGENT_BUILDER_DOTNET_PATH", directory: false));
        var dotnet = executable.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? executable.FullName;
        Assert.False(IsSameOrChildPath(dotnet, workerRuntimeRoot), "The project SDK must remain separate from the Host's Worker runtime.");
        var feed = RequiredMakerPath("SUNDER_AGENT_BUILDER_NUGET_FEED", directory: true);
        var feedHashes = Directory.GetFiles(feed, "*.nupkg").Order(StringComparer.Ordinal)
            .ToDictionary(path => Path.GetFileName(path), path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Equal(6, feedHashes.Count);
        var evidence = Path.Combine(metricsDirectory, "maker");
        Directory.CreateDirectory(evidence);
        var projectRoot = Path.Combine(testRoot, "NativeMakerSmoke");
        const string packageId = "native.maker.smoke";
        var tools = Path.Combine(GetWorkers(owner)[BuilderPackageId].WorkspaceRootPath, "development-toolchains", "project-tools");
        Directory.CreateDirectory(tools);
        await File.WriteAllTextAsync(Path.Combine(tools, "local-nuget.json"), JsonSerializer.Serialize(new { schemaVersion = 1, feedPath = feed }));

        var diagnostics = Assert.IsType<CapturingExecutionRuntimeLaunchResolver>(services.GetRequiredService<IExecutionRuntimeLaunchResolver>());
        string archive;
        byte[] lockBytes;
        await using (var maker = await NativeAppRpcProxy.CreateAsync(services, BuilderPackageId, rid, appConnection, Path.Combine(testRoot, "maker-app")))
        {
            var backend = (await InvokeMakerAsync(maker, diagnostics, "get-backend-id", JsonSerializer.SerializeToElement(new { }))).GetString()!;
            Assert.True(Guid.TryParseExact(backend, "N", out _));
            var created = await RunJobAsync("create");
            var entry = created.GetProperty("project").GetProperty("entryPoint").GetString()!;
            Assert.Equal(projectRoot, Path.GetDirectoryName(entry));
            Assert.True(File.Exists(Path.Combine(projectRoot, "Views", "DefaultPackageView.axaml")));
            Assert.True(Directory.Exists(created.GetProperty("developmentFolder").GetString()));
            var lockPath = Path.Combine(projectRoot, "sunder-contracts.lock.json");
            lockBytes = await File.ReadAllBytesAsync(lockPath);
            // Author only this newly scaffolded project. The SDK version is unchanged;
            // the explicit root also makes later jobs prove the same selected toolchain.
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "global.json"), JsonSerializer.Serialize(new
            {
                sdk = new { version = sdk, rollForward = "disable", allowPrerelease = false, paths = new[] { Path.GetDirectoryName(dotnet)! } },
            }));
            var servicePath = Path.Combine(projectRoot, "Services", "SampleService.cs");
            var source = await File.ReadAllTextAsync(servicePath);
            Assert.Contains("Hello, {name}!", source, StringComparison.Ordinal);
            await File.WriteAllTextAsync(servicePath, source.Replace("Hello, {name}!", "Maker authored, {name}!", StringComparison.Ordinal));
            var inspected = await InvokeMakerAsync(maker, diagnostics, "inspect", JsonSerializer.SerializeToElement(entry), content: true);
            Assert.Equal(packageId, inspected.GetProperty("packageId").GetString());
            var built = await RunJobAsync("build", entry, inspected);
            Assert.NotEqual(created.GetProperty("developmentContentIdentity").GetString(), built.GetProperty("developmentContentIdentity").GetString());
            var packed = await RunJobAsync("archive", entry, built.GetProperty("project"));
            archive = packed.GetProperty("archivePath").GetString()!;
            Assert.True(File.Exists(archive));
            Assert.Equal(lockBytes, await File.ReadAllBytesAsync(lockPath));

            async Task<JsonElement> RunJobAsync(string operation, string? entryPoint = null, JsonElement? expected = null)
            {
                var id = Guid.NewGuid().ToString("N");
                var request = JsonSerializer.SerializeToElement(JsonSerializer.Serialize(new
                {
                    id, backendId = backend, operation, kind = 0, projectFolder = projectRoot, entryPoint,
                    displayName = "Native Maker Smoke", packageId, expected,
                    installation = operation == "create" ? new
                    {
                        kind = 0, version = sdk, executablePath = dotnet, rootPath = Path.GetDirectoryName(dotnet)!, isManaged = false, isHealthy = true,
                    } : null,
                }, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
                var terminal = false;
                var accepted = false;
                Exception? failure = null;
                try
                {
                    // Exact retries must observe one finite job, never dispatch again.
                    Assert.Equal(id, (await InvokeMakerAsync(maker, diagnostics, "start-job", request)).GetString());
                    accepted = true;
                    Assert.Equal(id, (await InvokeMakerAsync(maker, diagnostics, "start-job", request)).GetString());
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
                    var result = await ObserveAsync(deadline.Token);
                    terminal = true;
                    await File.WriteAllTextAsync(Path.Combine(evidence, operation + ".json"), result.GetRawText());
                    Assert.True(result.GetProperty("state").GetString() == "succeeded", result.GetRawText());
                    Assert.Equal(sdk, result.GetProperty("installation").GetProperty("version").GetString());
                    Assert.Equal(dotnet, result.GetProperty("installation").GetProperty("executablePath").GetString());
                    Assert.Equal(id, (await InvokeMakerAsync(maker, diagnostics, "start-job", request)).GetString());
                    var retry = await InvokeMakerAsync(maker, diagnostics, "get-job",
                        JsonSerializer.SerializeToElement(new { jobId = id, expectedBackendId = backend }), content: true);
                    Assert.Equal(result.GetRawText(), retry.GetRawText());
                    return result;
                }
                catch (Exception error) { failure = error; throw; }
                finally
                {
                    if (accepted && !terminal)
                    {
                        try
                        {
                            await InvokeMakerAsync(maker, diagnostics, "cancel-job", JsonSerializer.SerializeToElement(new { jobId = id, expectedBackendId = backend }));
                            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                            _ = await ObserveAsync(cleanup.Token);
                        }
                        catch (Exception cleanup) when (failure is not null)
                        { throw new AggregateException("Maker job and bounded cancellation cleanup both failed.", failure, cleanup); }
                    }
                }
                async Task<JsonElement> ObserveAsync(CancellationToken token)
                {
                    while (true)
                    {
                        var result = await InvokeMakerAsync(maker, diagnostics, "get-job",
                            JsonSerializer.SerializeToElement(new { jobId = id, expectedBackendId = backend }), content: true, token);
                        Assert.Equal(id, result.GetProperty("id").GetString());
                        Assert.Equal(backend, result.GetProperty("backendId").GetString());
                        if (result.GetProperty("state").GetString() != "running") return result;
                        await Task.Delay(250, token);
                    }
                }
            }
        }

        var archiveHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(archive)));
        var lifecycle = services.GetRequiredService<PackageSessionLifecycleService>();
        var output = Path.Combine(testRoot, "created-package-source");
        await PrepareNativeDevelopmentPackageAsync(services, archive, output, []);
        var load = await lifecycle.LoadDevPackageAsync(output, watch: false);
        Assert.True(load.Success, string.Join(Environment.NewLine, load.Errors));
        CaptureWorkers(owner, observedProcesses, observedTemporaryPaths);
        var worker = GetWorkers(owner)[packageId];
        var childProcess = ProcessIdentity.Observe(WorkerReleaseMetrics.GetProcess(worker));
        await using (var child = await NativeAppRpcProxy.CreateAsync(services, packageId, rid, appConnection, Path.Combine(testRoot, "created-app")))
        {
            var result = await child.InvokeAsync(packageId + ".sample", "sample", "greet",
                JsonSerializer.SerializeToElement("native package"), content: false);
            Assert.Equal("Maker authored, native package!", result.GetString());
        }
        var remove = await lifecycle.UnloadDevPackageAsync(packageId);
        Assert.True(remove.Success, string.Join(Environment.NewLine, remove.Errors));
        await AssertProcessesStoppedAsync([childProcess]);
        Assert.Empty(await services.GetRequiredService<InstalledPackageLifecycleService>().GetInstalledAsync());
        Assert.Equal(BuilderPackageId, Assert.Single(GetWorkers(owner)).Key);
        foreach (var (name, hash) in feedHashes)
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(feed, name!)))));
        await File.WriteAllTextAsync(Path.Combine(evidence, "journey.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, rid, sdkVersion = sdk, nuget = feedHashes, packageId, archiveSha256 = archiveHash, withoutAgent = true,
            makerArchiveSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(
                ReadArchives(RequiredMakerPath("SUNDER_AGENT_NATIVE_ARCHIVE_DIR", directory: true), rid)[BuilderPackageId]))),
            profile = "unpublished-development-import", installedAcceptance = false,
            callerBoundary = "supervised-native-app-rpc", makerViewAcceptance = false,
            sourceLockSha256 = Convert.ToHexStringLower(SHA256.HashData(lockBytes)),
            operations = new[] { "create", "author", "build", "archive", "validate-extract", "activate-development", "invoke", "unload-development", "worker-stopped" },
        }));
    }

    private static async Task<SunderPackageArchiveValidationResult> PrepareNativeDevelopmentPackageAsync(IServiceProvider services,
        string archive, string folder, IReadOnlyList<string> candidates)
    {
        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archive, folder);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var required = (result.Manifest!.PackageArtifactImports ?? []).Select(value => value!.PackageId).ToHashSet(StringComparer.Ordinal);
        var selected = candidates.Where(path =>
        {
            using var candidate = ZipFile.OpenRead(path);
            using var stream = candidate.GetEntry("manifest/sunder-package.json")!.Open();
            using var manifest = JsonDocument.Parse(stream);
            return required.Contains(manifest.RootElement.GetProperty("id").GetString());
        }).ToArray();
        await services.GetRequiredService<DevelopmentImportAuthorityPreparation>().PrepareAsync(new(folder)
        {
            CandidateArchives = selected,
            PlatformCatalogPath = DevelopmentImportAuthorityStore.Platforms(result.Manifest).Length == 0
                ? null : RequiredMakerPath("SUNDER_AGENT_NATIVE_PLATFORM_CATALOG", directory: false),
        }, CancellationToken.None);
        return result;
    }

    private static string RequiredMakerPath(string variable, bool directory)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        Assert.False(string.IsNullOrWhiteSpace(value), variable + " must identify an explicit native Maker input.");
        var path = Path.GetFullPath(value!);
        Assert.True(directory ? Directory.Exists(path) : File.Exists(path), "Missing Maker input: " + path);
        return path;
    }

    private static async Task<JsonElement> InvokeMakerAsync(NativeAppRpcProxy client,
        CapturingExecutionRuntimeLaunchResolver diagnostics, string method, JsonElement request, bool content = false, CancellationToken token = default)
    {
        try { return await client.InvokeAsync(BuilderRuntimeContractId, "builder-runtime", method, request, content, token); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Maker {method} failed: {error.Message}; "
                + diagnostics.DescribeStandardError(BuilderPackageId), error);
        }
    }
}
