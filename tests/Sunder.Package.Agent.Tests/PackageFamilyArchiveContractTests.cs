using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class PackageFamilyArchiveContractTests
{
    internal const string ArchiveDirectoryVariable = "SUNDER_AGENT_PACKAGE_ARCHIVE_DIR";
    internal const string OutputAuditRequiredVariable = "SUNDER_AGENT_PACKAGE_OUTPUT_AUDIT_REQUIRED";

    private const string ContentIndexPath = "manifest/content-index.json";
    private const string DotNetModuleAbi = "sunder.dotnet-worker-module.v1";
    private const string DotNetRuntimeId = "sunder.runtime.dotnet";
    private const string DotNetRuntimeVersionRange = ">=10.0.0 <11.0.0";
    private const string FamilyDependencyRangeText = ">=2.0.0 <3.0.0";
    private const string ManifestPath = "manifest/sunder-package.json";
    private const long MaxArchiveBytes = 256L * 1024 * 1024;
    private const long MaxPayloadBytes = 640L * 1024 * 1024;
    private const long MaxFamilyBytes = 4L * 1024 * 1024 * 1024;
    private static readonly IReadOnlySet<string> BuildTimePathSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "analyzers", "build", "buildMultiTargeting", "buildTransitive", "obj", "packs", "ref", "refs", "sdk", "tools",
    };
    private static readonly IReadOnlySet<string> BuildTimeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".a", ".cs", ".csproj", ".exp", ".fs", ".fsproj", ".ilk", ".lib", ".props", ".rsp", ".targets", ".tasks", ".vb", ".vbproj",
    };
    private static readonly IReadOnlySet<string> ForbiddenRuntimeStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "apphost", "clrgc", "clrgcexp", "clrjit", "coreclr", "createdump", "dbgshim", "dotnet", "hostfxr", "hostpolicy",
        "mscordaccore", "mscordbi", "singlefilehost", "system.globalization.native", "system.io.compression.native", "system.native",
        "system.net.security.native", "system.private.corelib", "system.security.cryptography.native.apple",
        "system.security.cryptography.native.openssl",
    };
    private static readonly string[] SupportedRids =
    [
        "linux-arm64",
        "linux-x64",
        "osx-arm64",
        "osx-x64",
        "win-arm64",
        "win-x64",
    ];

    [PackageArtifactsFact]
    [Trait("Lane", "PackageCompatibility")]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "PackageArchive")]
    [Trait("OS", "Any")]
    [Trait("Size", "Large")]
    public void ReleaseArchives_MatchInventoryAndPayloadContract()
    {
        var archiveDirectory = ResolveArchiveDirectory();
        var inventory = LoadInventory();
        Assert.Equal(15, inventory.Packages.Count);
        Assert.Equal(
            inventory.Packages.Count,
            inventory.Packages.Select(static package => package.PackageId)
                .Distinct(StringComparer.Ordinal)
                .Count());

        var expectedNames = inventory.Packages
            .Select(package => $"{package.PackageId}.{inventory.ReleaseVersion}.sunderpkg")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualNames = Directory.EnumerateFiles(archiveDirectory, "*.sunderpkg", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedNames, actualNames);

        var packageIds = inventory.Packages
            .Select(static package => package.PackageId)
            .ToHashSet(StringComparer.Ordinal);
        long familyBytes = 0;
        foreach (var package in inventory.Packages)
        {
            var archivePath = Path.Combine(
                archiveDirectory,
                $"{package.PackageId}.{inventory.ReleaseVersion}.sunderpkg");
            var archiveBytes = new FileInfo(archivePath).Length;
            Assert.InRange(archiveBytes, 1, MaxArchiveBytes);
            familyBytes = checked(familyBytes + archiveBytes);

            using var archive = ZipFile.OpenRead(archivePath);
            VerifyArchive(
                archive,
                package,
                inventory.ReleaseVersion,
                packageIds,
                inventory.ContractIdentities);
        }

        Assert.InRange(familyBytes, 1, MaxFamilyBytes);
        VerifyMeasuredInstalledProjectionGate(archiveDirectory, inventory);
    }

    [PackageArtifactsFact]
    [Trait("Lane", "PackageCompatibility")]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "PackageArchive")]
    [Trait("OS", "Any")]
    [Trait("Size", "Large")]
    public void ContentIndexOracle_RejectsPayloadMutation()
    {
        var archiveDirectory = ResolveArchiveDirectory();
        var inventory = LoadInventory();
        var package = inventory.Packages[0];
        var sourcePath = Path.Combine(
            archiveDirectory,
            $"{package.PackageId}.{inventory.ReleaseVersion}.sunderpkg");
        using var temporary = TemporaryDirectory.Create("sunder-agent-archive-mutation");
        var mutatedPath = Path.Combine(temporary.Path, Path.GetFileName(sourcePath));
        RewriteArchive(sourcePath, mutatedPath, entries =>
        {
            var payload = entries.First(static entry =>
                entry.Path.StartsWith("payload/", StringComparison.Ordinal) && entry.Bytes.Length > 0);
            payload.Bytes[0] ^= 0x01;
        });

        using var archive = ZipFile.OpenRead(mutatedPath);
        var exception = Assert.Throws<InvalidDataException>(
            () =>
            {
                VerifyArchive(
                    archive,
                    package,
                    inventory.ReleaseVersion,
                    inventory.Packages.Select(static item => item.PackageId).ToHashSet(StringComparer.Ordinal),
                    inventory.ContractIdentities);
            });
        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [PackageArtifactsFact]
    [Trait("Lane", "PackageCompatibility")]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "PackageArchive")]
    [Trait("OS", "Any")]
    [Trait("Size", "Large")]
    public void FullArchiveOracle_RejectsNoncanonicalDotSegment()
    {
        var archiveDirectory = ResolveArchiveDirectory();
        var inventory = LoadInventory();
        var package = inventory.Packages[0];
        var sourcePath = Path.Combine(
            archiveDirectory,
            $"{package.PackageId}.{inventory.ReleaseVersion}.sunderpkg");
        using var temporary = TemporaryDirectory.Create("sunder-agent-archive-path-mutation");
        var mutatedPath = Path.Combine(temporary.Path, Path.GetFileName(sourcePath));
        RewriteArchive(sourcePath, mutatedPath, entries =>
        {
            var payload = entries.First(static entry => entry.Path.StartsWith("payload/", StringComparison.Ordinal));
            var separator = payload.Path.LastIndexOf('/');
            payload.Path = $"{payload.Path[..separator]}/./{payload.Path[(separator + 1)..]}";
        });

        using var archive = ZipFile.OpenRead(mutatedPath);
        var exception = Assert.Throws<InvalidDataException>(() =>
        {
            VerifyArchive(
                archive,
                package,
                inventory.ReleaseVersion,
                inventory.Packages.Select(static item => item.PackageId).ToHashSet(StringComparer.Ordinal),
                inventory.ContractIdentities);
        });
        Assert.Contains("noncanonical", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [PackageArtifactsFact]
    [Trait("Lane", "PackageCompatibility")]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "PackageArchive")]
    [Trait("OS", "Any")]
    [Trait("Size", "Large")]
    public void FullArchiveOracle_RejectsDescriptorIdentityMutation()
    {
        var archiveDirectory = ResolveArchiveDirectory();
        var inventory = LoadInventory();
        var package = inventory.Packages.First(static item => item.ProviderIds.Count > 0);
        var sourcePath = Path.Combine(
            archiveDirectory,
            $"{package.PackageId}.{inventory.ReleaseVersion}.sunderpkg");
        using var temporary = TemporaryDirectory.Create("sunder-agent-archive-descriptor-mutation");
        var mutatedPath = Path.Combine(temporary.Path, Path.GetFileName(sourcePath));
        RewriteArchive(sourcePath, mutatedPath, entries =>
        {
            var manifest = JsonNode.Parse(entries.Single(static entry => entry.Path == ManifestPath).Bytes)!.AsObject();
            manifest["contractBundles"]!.AsArray()[0]!["descriptorSha256"] = new string('0', 64);
            var manifestEntry = entries.Single(static entry => entry.Path == ManifestPath);
            manifestEntry.Bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            RefreshIndexedEntry(entries, manifestEntry);
        });

        using var archive = ZipFile.OpenRead(mutatedPath);
        var exception = Assert.Throws<InvalidDataException>(() =>
        {
            VerifyArchive(
                archive,
                package,
                inventory.ReleaseVersion,
                inventory.Packages.Select(static item => item.PackageId).ToHashSet(StringComparer.Ordinal),
                inventory.ContractIdentities);
        });
        Assert.Contains("descriptor identity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [PackageArtifactsFact]
    [Trait("Lane", "PackageCompatibility")]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "PackageArchive")]
    [Trait("OS", "Any")]
    [Trait("Size", "Large")]
    public void FullArchiveOracle_RejectsDependencyRangeMutation()
    {
        var archiveDirectory = ResolveArchiveDirectory();
        var inventory = LoadInventory();
        var package = inventory.Packages.First(static item =>
            !string.Equals(item.PackageId, "sunder.package.agent", StringComparison.Ordinal));
        var sourcePath = Path.Combine(
            archiveDirectory,
            $"{package.PackageId}.{inventory.ReleaseVersion}.sunderpkg");
        using var temporary = TemporaryDirectory.Create("sunder-agent-archive-dependency-mutation");
        var mutatedPath = Path.Combine(temporary.Path, Path.GetFileName(sourcePath));
        RewriteArchive(sourcePath, mutatedPath, entries =>
        {
            var manifestEntry = entries.Single(static entry => entry.Path == ManifestPath);
            var manifest = JsonNode.Parse(manifestEntry.Bytes)!.AsObject();
            manifest["dependsOn"]!.AsArray()[0]!["versionRange"] = ">=2.0.1 <3.0.0";
            manifestEntry.Bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            RefreshIndexedEntry(entries, manifestEntry);
        });

        using var archive = ZipFile.OpenRead(mutatedPath);
        var exception = Assert.ThrowsAny<Exception>(() =>
        {
            VerifyArchive(
                archive,
                package,
                inventory.ReleaseVersion,
                inventory.Packages.Select(static item => item.PackageId).ToHashSet(StringComparer.Ordinal),
                inventory.ContractIdentities);
        });
        Assert.Contains(FamilyDependencyRangeText, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Lane", "PackageCompatibility")]
    public void FamilyDependencyRange_IsIndependentOfPatchAndPrereleaseReleaseVersion()
    {
        foreach (var releaseVersion in new[]
                 {
                     "2.0.0",
                     "2.0.1",
                     "2.0.1-preview.1",
                     "2.1.0-alpha.1",
                     "2.9.0-rc.2+build.7",
                 })
        {
            VerifyFamilyDependencyRange(releaseVersion, FamilyDependencyRangeText, "test.package");
        }
    }

    [Fact]
    [Trait("Lane", "PackageCompatibility")]
    public void FamilyDependencyRange_RejectsIncorrectRanges()
    {
        foreach (var versionRange in new[]
                 {
                     ">=2.0.1 <3.0.0",
                     ">=2.0.0 <2.1.0",
                     ">=2.0.0",
                     "[2.0.0,3.0.0)",
                 })
        {
            Assert.ThrowsAny<Exception>(() =>
                VerifyFamilyDependencyRange("2.0.1-preview.1", versionRange, "test.package"));
        }

        foreach (var releaseVersion in new[]
                 {
                     "1.99.99",
                     "2.0.0-preview.1",
                     "2.0.0-preview.99+build.7",
                     "3.0.0",
                 })
        {
            var exception = Assert.Throws<InvalidDataException>(() =>
                VerifyFamilyDependencyRange(releaseVersion, FamilyDependencyRangeText, "test.package"));
            Assert.Contains("does not satisfy", exception.Message, StringComparison.Ordinal);
        }
    }

    private static void VerifyArchive(
        ZipArchive archive,
        PackageExpectation package,
        string releaseVersion,
        IReadOnlySet<string> packageIds,
        IReadOnlyDictionary<string, ContractIdentity> contractIdentities)
    {
        var paths = archive.Entries.Select(static entry => entry.FullName).ToArray();
        Assert.NotEmpty(paths);
        foreach (var path in paths)
        {
            ValidateCanonicalArchivePath(path, package.PackageId);
        }
        Assert.Equal(paths.Order(StringComparer.Ordinal).ToArray(), paths);
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(archive.Entries, static entry =>
        {
            Assert.False(entry.FullName.EndsWith("/", StringComparison.Ordinal));
            Assert.Equal(new DateTime(1980, 1, 1), entry.LastWriteTime.DateTime);
            Assert.Equal(0, entry.ExternalAttributes);
        });
        Assert.DoesNotContain(paths, static path =>
            !path.StartsWith("payload/app/", StringComparison.Ordinal)
            && (path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase)));
        Assert.All(paths, static path => Assert.True(
            string.Equals(path, ContentIndexPath, StringComparison.Ordinal)
            || string.Equals(path, ManifestPath, StringComparison.Ordinal)
            || path.StartsWith("payload/shared/", StringComparison.Ordinal)
            || path.StartsWith("payload/app/", StringComparison.Ordinal)
            || path.StartsWith("payload/runtime/", StringComparison.Ordinal),
            $"Archive path '{path}' is outside the package payload contract."));
        foreach (var xmlPath in paths.Where(static path => path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            var assemblyPath = $"{xmlPath[..^4]}.dll";
            Assert.DoesNotContain(assemblyPath, paths, StringComparer.OrdinalIgnoreCase);
        }

        var payloadBytes = ValidateContentIndex(archive, package.PackageId);
        Assert.InRange(payloadBytes, 1, MaxPayloadBytes);
        VerifyNoRuntimeOrToolchainPayload(paths, package);
        VerifyPackageOwnedPayload(archive, paths, package);
        using var manifest = ReadJson(archive, ManifestPath, package.PackageId);
        VerifyManifest(
            archive,
            manifest.RootElement,
            package,
            releaseVersion,
            packageIds,
            contractIdentities);
    }

    private static void VerifyMeasuredInstalledProjectionGate(
        string archiveDirectory,
        PackageInventory inventory)
    {
        const long phase0BaselineBytes = 1_440_013_254;
        const long maximumInstalledProjectionBytes = 360_003_313;
        var auditPath = Path.Combine(archiveDirectory, "package-output-audit.json");
        if (!File.Exists(auditPath))
        {
            Assert.NotEqual(
                "1",
                Environment.GetEnvironmentVariable(OutputAuditRequiredVariable));
            return;
        }
        using var audit = JsonDocument.Parse(File.ReadAllBytes(auditPath));
        var root = audit.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("https://sunderapp.io/evidence/agent-package-output-audit/v1", root.GetProperty("kind").GetString());
        Assert.Equal(inventory.ReleaseVersion, root.GetProperty("version").GetString());
        var gate = root.GetProperty("installedProjectionGate");
        Assert.Equal(phase0BaselineBytes, gate.GetProperty("phase0BaselineBytes").GetInt64());
        Assert.Equal(75, gate.GetProperty("requiredReductionPercent").GetInt32());
        Assert.Equal(maximumInstalledProjectionBytes, gate.GetProperty("maximumBytes").GetInt64());
        var familyRids = root.GetProperty("family").GetProperty("rids").EnumerateArray().ToArray();
        Assert.Equal(
            SupportedRids,
            familyRids.Select(static rid => RequiredString(rid, "rid")).Order(StringComparer.Ordinal).ToArray());
        Assert.All(familyRids, rid =>
        {
            Assert.True(rid.GetProperty("meetsInstalledProjectionGate").GetBoolean());
            Assert.InRange(
                rid.GetProperty("installedProjection").GetProperty("bytes").GetInt64(),
                1,
                maximumInstalledProjectionBytes);
        });
        Assert.Equal(
            inventory.Packages.Select(static package => package.PackageId).Order(StringComparer.Ordinal).ToArray(),
            root.GetProperty("packages").EnumerateArray()
                .Select(static package => RequiredString(package, "packageId"))
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    private static void VerifyManifest(
        ZipArchive archive,
        JsonElement root,
        PackageExpectation package,
        string releaseVersion,
        IReadOnlySet<string> packageIds,
        IReadOnlyDictionary<string, ContractIdentity> contractIdentities)
    {
        Assert.Equal(1, root.GetProperty("archiveFormatVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("manifestVersion").GetInt32());
        Assert.Equal(package.PackageId, root.GetProperty("id").GetString());
        Assert.Equal(releaseVersion, root.GetProperty("version").GetString());

        var dependencies = root.GetProperty("dependsOn").EnumerateArray().ToArray();
        var dependencyIds = dependencies
            .Select(static dependency => RequiredString(dependency, "packageId"))
            .ToArray();
        Assert.Equal(dependencyIds.Length, dependencyIds.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(package.PackageId, dependencyIds, StringComparer.Ordinal);
        Assert.All(dependencyIds, packageId => Assert.Contains(packageId, packageIds));
        Assert.All(dependencies, dependency => VerifyFamilyDependencyRange(
            releaseVersion,
            RequiredString(dependency, "versionRange"),
            RequiredString(dependency, "packageId")));

        var targets = root.GetProperty("targets").EnumerateArray().ToArray();
        var runtimeTargets = targets
            .Where(static target => RequiredString(target, "role") == "runtime")
            .ToArray();
        var appTargets = targets
            .Where(static target => RequiredString(target, "role") == "app")
            .ToArray();
        Assert.Equal(
            SupportedRids,
            runtimeTargets.Select(static target => RequiredString(target, "rid")).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            package.HasApp ? SupportedRids : [],
            appTargets.Select(static target => RequiredString(target, "rid")).Order(StringComparer.Ordinal).ToArray());
        Assert.All(runtimeTargets, target =>
        {
            Assert.Equal("worker", RequiredString(target, "kind"));
            Assert.Equal("net10.0", RequiredString(target, "targetFramework"));
            Assert.Equal($"lib/{package.WorkerAssemblyName}.dll", RequiredString(target, "entryPoint"));
            var executionRuntime = target.GetProperty("executionRuntime");
            Assert.Equal(DotNetRuntimeId, RequiredString(executionRuntime, "id"));
            Assert.Equal(DotNetRuntimeVersionRange, RequiredString(executionRuntime, "versionRange"));
            Assert.Equal(DotNetModuleAbi, RequiredString(executionRuntime, "moduleAbi"));
            Assert.Contains(
                "worker-protocol.v1",
                target.GetProperty("requiredHostCapabilities").EnumerateArray()
                    .Select(static capability => capability.GetString()),
                StringComparer.Ordinal);
            VerifyDotNetWorkerPayload(archive, target, package);
        });
        Assert.All(appTargets, static target =>
        {
            Assert.Equal("avalonia", RequiredString(target, "kind"));
            Assert.Equal("net10.0", RequiredString(target, "targetFramework"));
            Assert.DoesNotContain(
                "worker-protocol.v1",
                target.GetProperty("requiredHostCapabilities").EnumerateArray()
                    .Select(static capability => capability.GetString()),
                StringComparer.Ordinal);
        });

        var providers = root.GetProperty("provides").EnumerateArray().ToArray();
        Assert.Equal(
            package.ProviderIds,
            providers.Select(static provider => RequiredString(provider, "providerId")).Order(StringComparer.Ordinal).ToArray());
        VerifyContractHashes(archive, root, providers, package.PackageId, contractIdentities);
    }

    private static void VerifyDotNetWorkerPayload(
        ZipArchive archive,
        JsonElement target,
        PackageExpectation package)
    {
        var rid = RequiredString(target, "rid");
        var entryPoint = RequiredString(target, "entryPoint");
        var moduleBase = entryPoint[..^".dll".Length];
        _ = ResolveProjectedEntry(archive, "runtime", rid, entryPoint, package.PackageId);
        AssertProjectedEntryMissing(archive, "runtime", rid, moduleBase, package.PackageId);
        AssertProjectedEntryMissing(archive, "runtime", rid, moduleBase + ".exe", package.PackageId);

        var runtimeConfigPath = moduleBase + ".runtimeconfig.json";
        var runtimeConfigEntry = ResolveProjectedEntry(archive, "runtime", rid, runtimeConfigPath, package.PackageId);
        using (var runtimeConfig = ReadJson(archive, runtimeConfigEntry.FullName, package.PackageId))
        {
            var runtimeOptions = runtimeConfig.RootElement.GetProperty("runtimeOptions");
            Assert.Equal("net10.0", RequiredString(runtimeOptions, "tfm"));
            Assert.False(runtimeOptions.TryGetProperty("includedFrameworks", out _));
            Assert.False(runtimeOptions.TryGetProperty("frameworks", out _));
            var framework = runtimeOptions.GetProperty("framework");
            Assert.Equal("Microsoft.NETCore.App", RequiredString(framework, "name"));
            Assert.StartsWith("10.", RequiredString(framework, "version"), StringComparison.Ordinal);
        }

        var depsPath = moduleBase + ".deps.json";
        var depsEntry = ResolveProjectedEntry(archive, "runtime", rid, depsPath, package.PackageId);
        using var dependencies = ReadJson(archive, depsEntry.FullName, package.PackageId);
        var expectedRuntimeTarget = $".NETCoreApp,Version=v10.0/{rid}";
        Assert.Equal(
            expectedRuntimeTarget,
            RequiredString(dependencies.RootElement.GetProperty("runtimeTarget"), "name"));
        var targetGraph = dependencies.RootElement.GetProperty("targets").GetProperty(expectedRuntimeTarget);
        var packageOwnedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in targetGraph.EnumerateObject())
        {
            VerifyDependencyIdentity(library.Name, package.PackageId, rid);
            if (library.Value.TryGetProperty("dependencies", out var transitiveDependencies))
            {
                foreach (var dependency in transitiveDependencies.EnumerateObject())
                {
                    VerifyDependencyIdentity(dependency.Name, package.PackageId, rid);
                }
            }
            foreach (var assetGroup in new[] { "runtime", "native", "resources", "runtimeTargets" })
            {
                if (!library.Value.TryGetProperty(assetGroup, out var assets)
                    || assets.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                foreach (var asset in assets.EnumerateObject())
                {
                    if (assetGroup == "runtimeTargets"
                        && asset.Value.TryGetProperty("rid", out var assetRid)
                        && assetRid.ValueKind == JsonValueKind.String
                        && !string.Equals(assetRid.GetString(), rid, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    packageOwnedAssets.Add(FileName(asset.Name));
                }
            }
        }

        foreach (var entry in ProjectedEntries(archive, "runtime", rid))
        {
            var fileName = FileName(entry.FullName);
            if (IsPotentialSharedFrameworkAssembly(fileName))
            {
                Require(
                    packageOwnedAssets.Contains(fileName),
                    $"{package.PackageId}/{rid}: shared-framework assembly '{fileName}' is not package-owned by the RID dependency graph.");
            }
        }
    }

    private static void VerifyNoRuntimeOrToolchainPayload(
        IReadOnlyList<string> paths,
        PackageExpectation package)
    {
        foreach (var path in paths.Where(static path =>
                     path.StartsWith("payload/runtime/", StringComparison.Ordinal)
                     || path.StartsWith("payload/app/", StringComparison.Ordinal)
                     || path.StartsWith("payload/shared/", StringComparison.Ordinal)))
        {
            var segments = path.Split('/');
            Require(
                !segments.Any(BuildTimePathSegments.Contains)
                && !BuildTimeExtensions.Contains(Path.GetExtension(path)),
                $"{package.PackageId}: package payload contains build-time or toolchain path '{path}'.");
            if (path.StartsWith("payload/runtime/", StringComparison.Ordinal)
                || path.StartsWith("payload/shared/", StringComparison.Ordinal))
            {
                var stem = RuntimeStem(FileName(path));
                Require(
                    !ForbiddenRuntimeStems.Contains(stem)
                    && !stem.StartsWith("mscordaccore_", StringComparison.OrdinalIgnoreCase)
                    && !stem.StartsWith("mscordbi_", StringComparison.OrdinalIgnoreCase),
                    $"{package.PackageId}: Worker payload contains forbidden .NET runtime file '{path}'.");
            }
        }

        if (package.PackageId == "sunder.package.agent.builder")
        {
            Assert.DoesNotContain(paths, static path =>
            {
                var fileName = FileName(path);
                return fileName.StartsWith("Microsoft.Build", StringComparison.OrdinalIgnoreCase)
                       || fileName.StartsWith("NuGet.", StringComparison.OrdinalIgnoreCase)
                       || fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                       || fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    private static void VerifyPackageOwnedPayload(
        ZipArchive archive,
        IReadOnlyList<string> paths,
        PackageExpectation package)
    {
        var sqliteOwner = package.PackageId is "sunder.package.agent" or "sunder.package.agent.memory.semantic";
        var sqlitePaths = paths.Where(static path =>
            FileName(path).StartsWith("SQLitePCLRaw.", StringComparison.OrdinalIgnoreCase)
            || FileName(path).Contains("e_sqlite3", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sqliteOwner)
        {
            Assert.Contains(sqlitePaths, static path => FileName(path).Equals("SQLitePCLRaw.core.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(sqlitePaths, static path => FileName(path).Equals("SQLitePCLRaw.provider.e_sqlite3.dll", StringComparison.OrdinalIgnoreCase));
            foreach (var rid in SupportedRids)
            {
                var nativeName = rid.StartsWith("win-", StringComparison.Ordinal)
                    ? "e_sqlite3.dll"
                    : rid.StartsWith("osx-", StringComparison.Ordinal)
                        ? "libe_sqlite3.dylib"
                        : "libe_sqlite3.so";
                _ = ResolveProjectedEntry(archive, "runtime", rid, $"lib/{nativeName}", package.PackageId);
            }
        }
        else
        {
            Assert.Empty(sqlitePaths);
        }

        var onigwrapOwner = package.PackageId is "sunder.package.agent" or "sunder.package.agent.mcp" or "sunder.package.agent.subagents";
        var onigwrapPaths = paths.Where(static path =>
            FileName(path).Contains("onigwrap", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (onigwrapOwner)
        {
            _ = ResolveProjectedEntry(archive, "app", SupportedRids[0], "lib/Onigwrap.dll", package.PackageId);
            foreach (var rid in SupportedRids)
            {
                var nativeName = rid.StartsWith("win-", StringComparison.Ordinal)
                    ? "libonigwrap.dll"
                    : rid.StartsWith("osx-", StringComparison.Ordinal)
                        ? "libonigwrap.dylib"
                        : "libonigwrap.so";
                _ = ResolveProjectedEntry(
                    archive,
                    "app",
                    rid,
                    $"lib/runtimes/{rid}/native/{nativeName}",
                    package.PackageId);
            }
        }
        else
        {
            Assert.Empty(onigwrapPaths);
        }

        var systemManagementPaths = paths.Where(static path =>
            FileName(path).Equals("System.Management.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (package.PackageId == "sunder.package.agent.provider.gemini")
        {
            Assert.NotEmpty(systemManagementPaths);
            foreach (var rid in SupportedRids)
            {
                _ = ResolveProjectedEntry(archive, "runtime", rid, "lib/System.Management.dll", package.PackageId);
            }
        }
        else
        {
            Assert.Empty(systemManagementPaths);
        }
    }

    private static ZipArchiveEntry ResolveProjectedEntry(
        ZipArchive archive,
        string role,
        string rid,
        string logicalPath,
        string packageId)
    {
        var candidates = new[]
        {
            archive.GetEntry($"payload/{role}/{rid}/{logicalPath}"),
            archive.GetEntry($"payload/{role}/shared/{logicalPath}"),
            archive.GetEntry($"payload/shared/{logicalPath}"),
        }.Where(static entry => entry is not null).Cast<ZipArchiveEntry>().ToArray();
        Require(
            candidates.Length == 1,
            $"{packageId}: target '{role}/{rid}' must resolve exactly one '{logicalPath}', but found {candidates.Length}.");
        return candidates[0];
    }

    private static void AssertProjectedEntryMissing(
        ZipArchive archive,
        string role,
        string rid,
        string logicalPath,
        string packageId)
    {
        Require(
            archive.GetEntry($"payload/{role}/{rid}/{logicalPath}") is null
            && archive.GetEntry($"payload/{role}/shared/{logicalPath}") is null
            && archive.GetEntry($"payload/shared/{logicalPath}") is null,
            $"{packageId}: target '{role}/{rid}' contains forbidden projected entry '{logicalPath}'.");
    }

    private static IEnumerable<ZipArchiveEntry> ProjectedEntries(ZipArchive archive, string role, string rid)
    {
        var targetPrefix = $"payload/{role}/{rid}/";
        var sharedPrefix = $"payload/{role}/shared/";
        return archive.Entries.Where(entry =>
            entry.FullName.StartsWith(targetPrefix, StringComparison.Ordinal)
            || entry.FullName.StartsWith(sharedPrefix, StringComparison.Ordinal)
            || entry.FullName.StartsWith("payload/shared/", StringComparison.Ordinal));
    }

    private static void VerifyDependencyIdentity(string identity, string packageId, string rid)
    {
        var separator = identity.LastIndexOf('/');
        var dependencyId = separator > 0 ? identity[..separator] : identity;
        var runtimePack = dependencyId.StartsWith("runtimepack.", StringComparison.OrdinalIgnoreCase)
                          || dependencyId.StartsWith("Microsoft.NETCore.App.Runtime.", StringComparison.OrdinalIgnoreCase)
                          || dependencyId.StartsWith("Microsoft.NETCore.App.Host.", StringComparison.OrdinalIgnoreCase)
                          || dependencyId.Equals("Microsoft.NETCore.App.Ref", StringComparison.OrdinalIgnoreCase)
                          || dependencyId.StartsWith("Microsoft.AspNetCore.App.Runtime.", StringComparison.OrdinalIgnoreCase)
                          || dependencyId.Equals("Microsoft.AspNetCore.App.Ref", StringComparison.OrdinalIgnoreCase)
                          || dependencyId.StartsWith("Microsoft.WindowsDesktop.App.Runtime.", StringComparison.OrdinalIgnoreCase)
                          || dependencyId.Equals("Microsoft.WindowsDesktop.App.Ref", StringComparison.OrdinalIgnoreCase)
                          || dependencyId.StartsWith("Microsoft.NETCore.DotNetHost", StringComparison.OrdinalIgnoreCase)
                          || dependencyId.StartsWith("Microsoft.NETCore.DotNetAppHost", StringComparison.OrdinalIgnoreCase);
        var toolchain = dependencyId.Equals("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase)
                        || dependencyId.StartsWith("Microsoft.NET.Sdk.", StringComparison.OrdinalIgnoreCase)
                        || dependencyId.Equals("Microsoft.Build", StringComparison.OrdinalIgnoreCase)
                        || dependencyId.StartsWith("Microsoft.Build.", StringComparison.OrdinalIgnoreCase)
                        || dependencyId.StartsWith("NuGet.", StringComparison.OrdinalIgnoreCase);
        Require(!runtimePack, $"{packageId}/{rid}: dependency graph contains runtime-pack identity '{identity}'.");
        Require(!toolchain, $"{packageId}/{rid}: dependency graph contains SDK/toolchain identity '{identity}'.");
    }

    private static bool IsPotentialSharedFrameworkAssembly(string fileName)
        => fileName.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
           || fileName.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase)
           || fileName.Equals("WindowsBase.dll", StringComparison.OrdinalIgnoreCase)
           || fileName.Equals("Microsoft.CSharp.dll", StringComparison.OrdinalIgnoreCase)
           || fileName.Equals("Microsoft.VisualBasic.dll", StringComparison.OrdinalIgnoreCase)
           || fileName.Equals("Microsoft.VisualBasic.Core.dll", StringComparison.OrdinalIgnoreCase)
           || fileName.StartsWith("Microsoft.Win32.", StringComparison.OrdinalIgnoreCase)
           || fileName.Equals("System.dll", StringComparison.OrdinalIgnoreCase)
           || fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
           && fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static string RuntimeStem(string fileName)
    {
        var stem = fileName.ToLowerInvariant();
        var sharedObject = stem.IndexOf(".so", StringComparison.Ordinal);
        if (sharedObject > 0)
        {
            stem = stem[..sharedObject];
        }
        else
        {
            foreach (var extension in new[] { ".dylib", ".dll", ".exe" })
            {
                if (!stem.EndsWith(extension, StringComparison.Ordinal)) continue;
                stem = stem[..^extension.Length];
                break;
            }
        }
        return stem.StartsWith("lib", StringComparison.Ordinal) ? stem[3..] : stem;
    }

    private static string FileName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    private static void VerifyContractHashes(
        ZipArchive archive,
        JsonElement root,
        IReadOnlyList<JsonElement> providers,
        string packageId,
        IReadOnlyDictionary<string, ContractIdentity> contractIdentities)
    {
        var bundles = root.GetProperty("contractBundles").EnumerateArray().ToArray();
        var bundleKeys = bundles.Select(ContractKey).ToArray();
        Assert.Equal(bundleKeys.Length, bundleKeys.Distinct(StringComparer.Ordinal).Count());
        var bundlesByKey = bundles.ToDictionary(ContractKey, StringComparer.Ordinal);

        foreach (var bundle in bundles)
        {
            var relativeDescriptorPath = RequiredString(bundle, "descriptorPath");
            ValidateCanonicalArchivePath(relativeDescriptorPath, packageId);
            var descriptorPath = $"payload/shared/{relativeDescriptorPath}";
            var descriptor = archive.GetEntry(descriptorPath);
            Assert.True(descriptor is not null, $"{packageId}: contract descriptor '{descriptorPath}' is missing.");
            var descriptorBytes = ReadBytes(descriptor!);
            var parsed = SunderRpcContractDescriptor.Parse(descriptorBytes);
            var contractId = RequiredString(bundle, "contractId");
            var version = RequiredString(bundle, "version");
            var descriptorSha256 = RequiredString(bundle, "descriptorSha256");
            var abiSha256 = RequiredString(bundle, "abiSha256");
            Require(parsed.ContractId == contractId, $"{packageId}: archived descriptor contract id does not match '{contractId}'.");
            Require(parsed.Version == version, $"{packageId}: archived descriptor version does not match '{version}'.");
            Require(
                string.Equals(descriptorSha256, Hash(descriptorBytes), StringComparison.Ordinal)
                && string.Equals(descriptorSha256, parsed.Sha256, StringComparison.Ordinal),
                $"{packageId}: descriptor identity for '{contractId}@{version}' was not recomputed from archive bytes.");
            Require(
                string.Equals(abiSha256, parsed.AbiSha256, StringComparison.Ordinal),
                $"{packageId}: ABI identity for '{contractId}@{version}' was not recomputed from archive bytes.");
            if (contractIdentities.TryGetValue(ContractKey(contractId, version), out var candidate))
            {
                Require(
                    descriptorSha256 == candidate.DescriptorSha256 && abiSha256 == candidate.AbiSha256,
                    $"{packageId}: descriptor identity for '{contractId}@{version}' does not match the compatibility candidate.");
            }
            else
            {
                Require(
                    !contractId.StartsWith("sunder.agent.", StringComparison.Ordinal),
                    $"{packageId}: Agent descriptor '{contractId}@{version}' is absent from the compatibility candidate.");
            }
        }

        foreach (var provider in providers)
        {
            var key = ContractKey(provider, "contractVersion");
            Assert.True(
                bundlesByKey.TryGetValue(key, out var bundle),
                $"{packageId}: provider '{RequiredString(provider, "providerId")}' has no matching contract bundle.");
            Assert.Equal(RequiredString(bundle, "descriptorSha256"), RequiredString(provider, "descriptorSha256"));
            Assert.Equal(RequiredString(bundle, "abiSha256"), RequiredString(provider, "abiSha256"));
        }

        foreach (var use in root.GetProperty("usesContracts").EnumerateArray())
        {
            AssertSha256(RequiredString(use, "abiSha256"));
            if (bundlesByKey.TryGetValue(ContractKey(use, "contractVersion"), out var bundle))
            {
                Assert.Equal(RequiredString(bundle, "abiSha256"), RequiredString(use, "abiSha256"));
            }
        }
    }

    private static long ValidateContentIndex(ZipArchive archive, string packageId)
    {
        var entries = archive.Entries.ToDictionary(static entry => entry.FullName, StringComparer.Ordinal);
        using var index = ReadJson(archive, ContentIndexPath, packageId);
        var indexedPaths = new HashSet<string>(StringComparer.Ordinal);
        long payloadBytes = 0;
        foreach (var file in index.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = RequiredString(file, "path");
            ValidateCanonicalArchivePath(path, packageId);
            Require(indexedPaths.Add(path), $"{packageId}: content index repeats '{path}'.");
            if (!entries.TryGetValue(path, out var entry))
            {
                throw new InvalidDataException($"{packageId}: indexed file '{path}' is missing.");
            }
            var size = file.GetProperty("size").GetInt64();
            Require(size == entry.Length, $"{packageId}: size mismatch for '{path}'.");
            var expectedHash = RequiredString(file, "sha256");
            AssertSha256Value(expectedHash, packageId, path);
            Require(
                string.Equals(expectedHash, Hash(entry), StringComparison.Ordinal),
                $"{packageId}: SHA-256 mismatch for '{path}'.");
            payloadBytes = checked(payloadBytes + size);
        }

        var expectedPaths = entries.Keys
            .Where(static path => !string.Equals(path, ContentIndexPath, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        Require(
            expectedPaths.SetEquals(indexedPaths),
            $"{packageId}: content index does not cover the exact archive payload.");
        return payloadBytes;
    }

    private static void ValidateCanonicalArchivePath(string path, string packageId)
    {
        Require(!string.IsNullOrWhiteSpace(path), $"{packageId}: archive path is empty.");
        Require(
            !path.StartsWith("/", StringComparison.Ordinal)
            && !path.Contains('\\')
            && !path.EndsWith("/", StringComparison.Ordinal),
            $"{packageId}: archive path '{path}' is noncanonical.");
        var segments = path.Split('/');
        Require(
            segments.All(static segment => segment.Length > 0 && segment is not "." and not ".."),
            $"{packageId}: archive path '{path}' contains a noncanonical segment.");
    }

    private static void RewriteArchive(
        string sourcePath,
        string destinationPath,
        Action<List<MutableArchiveEntry>> mutate)
    {
        List<MutableArchiveEntry> entries;
        using (var source = ZipFile.OpenRead(sourcePath))
        {
            entries = source.Entries
                .Select(entry => new MutableArchiveEntry(entry.FullName, ReadBytes(entry)))
                .ToList();
        }

        mutate(entries);
        using var destination = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
        foreach (var entry in entries.OrderBy(static entry => entry.Path, StringComparer.Ordinal))
        {
            var outputEntry = destination.CreateEntry(entry.Path, CompressionLevel.Optimal);
            outputEntry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            outputEntry.ExternalAttributes = 0;
            using var output = outputEntry.Open();
            output.Write(entry.Bytes);
        }
    }

    private static void RefreshIndexedEntry(
        IReadOnlyList<MutableArchiveEntry> entries,
        MutableArchiveEntry changedEntry)
    {
        var indexEntry = entries.Single(static entry => entry.Path == ContentIndexPath);
        var index = JsonNode.Parse(indexEntry.Bytes)!.AsObject();
        var indexedFile = index["files"]!.AsArray()
            .Select(static node => node!.AsObject())
            .Single(file => file["path"]!.GetValue<string>() == changedEntry.Path);
        indexedFile["size"] = changedEntry.Bytes.LongLength;
        indexedFile["sha256"] = Hash(changedEntry.Bytes);
        indexEntry.Bytes = JsonSerializer.SerializeToUtf8Bytes(index);
    }

    private static void VerifyFamilyDependencyRange(
        string releaseVersion,
        string versionRange,
        string packageId)
    {
        var release = SemanticVersion.Parse(releaseVersion);
        var expected = PackageVersionRange.Parse(FamilyDependencyRangeText);
        var actual = PackageVersionRange.Parse(versionRange);
        Require(
            expected.IsSatisfiedBy(release),
            $"{packageId}: release version '{releaseVersion}' does not satisfy the Agent family contract '{FamilyDependencyRangeText}'.");
        Require(
            actual.Equals(expected),
            $"{packageId}: dependency range must be the Agent family contract '{FamilyDependencyRangeText}', not '{versionRange}'.");
    }

    private static JsonDocument ReadJson(ZipArchive archive, string path, string packageId)
    {
        var entry = archive.GetEntry(path);
        if (entry is null)
        {
            throw new InvalidDataException($"{packageId}: archive entry '{path}' is missing.");
        }

        using var stream = entry.Open();
        return JsonDocument.Parse(stream);
    }

    private static string Hash(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void AssertSha256(string value)
        => Assert.Matches("^[0-9a-f]{64}$", value);

    private static void AssertSha256Value(string value, string packageId, string path)
        => Require(
            value.Length == 64 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            $"{packageId}: '{path}' has an invalid SHA-256 value.");

    private static string ContractKey(JsonElement contract)
        => ContractKey(contract, "version");

    private static string ContractKey(JsonElement contract, string versionProperty)
        => $"{RequiredString(contract, "contractId")}\0{RequiredString(contract, versionProperty)}";

    private static string ContractKey(string contractId, string version)
        => $"{contractId}\0{version}";

    private static string RequiredString(JsonElement element, string propertyName)
        => element.GetProperty(propertyName).GetString()
           ?? throw new InvalidDataException($"Property '{propertyName}' must be a string.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    private static string ResolveArchiveDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(ArchiveDirectoryVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            TestPrerequisites.ThrowIfRequired(
                $"Set {ArchiveDirectoryVariable} to the complete package archive directory.");
            throw new InvalidOperationException($"{ArchiveDirectoryVariable} is not configured.");
        }

        var fullPath = Path.GetFullPath(configured);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"Package archive directory '{fullPath}' from {ArchiveDirectoryVariable} does not exist.");
        }

        return fullPath;
    }

    private static PackageInventory LoadInventory()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "packages.json")));
        var root = document.RootElement;
        var packages = root.GetProperty("packages").EnumerateArray()
            .Where(static package => RequiredString(package, "artifactType") == "sunderpkg")
            .Select(static package =>
            {
                var projectPath = RequiredString(package, "projectPath");
                return new PackageExpectation(
                    RequiredString(package, "packageId"),
                    Path.GetFileNameWithoutExtension(projectPath),
                    package.TryGetProperty("appProjectPath", out var appProjectPath)
                    && !string.IsNullOrWhiteSpace(appProjectPath.GetString()),
                    package.GetProperty("providerIds").EnumerateArray()
                        .Select(static providerId => providerId.GetString()!)
                        .Order(StringComparer.Ordinal)
                        .ToArray());
            })
            .OrderBy(static package => package.PackageId, StringComparer.Ordinal)
            .ToArray();
        using var candidateDocument = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "compatibility",
            "agent-compatibility-candidate.json")));
        var contractIdentities = candidateDocument.RootElement.GetProperty("descriptors")
            .EnumerateArray()
            .Select(static descriptor => new ContractIdentity(
                RequiredString(descriptor, "contractId"),
                RequiredString(descriptor, "version"),
                RequiredString(descriptor, "descriptorSha256"),
                RequiredString(descriptor, "abiSha256")))
            .ToDictionary(
                static identity => ContractKey(identity.ContractId, identity.Version),
                StringComparer.Ordinal);
        return new PackageInventory(
            RequiredString(root, "releaseVersion"),
            packages,
            contractIdentities);
    }

    private sealed record PackageInventory(
        string ReleaseVersion,
        IReadOnlyList<PackageExpectation> Packages,
        IReadOnlyDictionary<string, ContractIdentity> ContractIdentities);

    private sealed record PackageExpectation(
        string PackageId,
        string WorkerAssemblyName,
        bool HasApp,
        IReadOnlyList<string> ProviderIds);

    private sealed record ContractIdentity(
        string ContractId,
        string Version,
        string DescriptorSha256,
        string AbiSha256);

    private sealed class MutableArchiveEntry(string path, byte[] bytes)
    {
        public string Path { get; set; } = path;

        public byte[] Bytes { get; set; } = bytes;
    }
}
