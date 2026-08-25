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

    private const string ContentIndexPath = "manifest/content-index.json";
    private const string FamilyDependencyRangeText = ">=2.0.0 <3.0.0";
    private const string ManifestPath = "manifest/sunder-package.json";
    private const long MaxArchiveBytes = 256L * 1024 * 1024;
    private const long MaxPayloadBytes = 640L * 1024 * 1024;
    private const long MaxFamilyBytes = 4L * 1024 * 1024 * 1024;
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
            () => VerifyArchive(
                archive,
                package,
                inventory.ReleaseVersion,
                inventory.Packages.Select(static item => item.PackageId).ToHashSet(StringComparer.Ordinal),
                inventory.ContractIdentities));
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
        var exception = Assert.Throws<InvalidDataException>(() => VerifyArchive(
            archive,
            package,
            inventory.ReleaseVersion,
            inventory.Packages.Select(static item => item.PackageId).ToHashSet(StringComparer.Ordinal),
            inventory.ContractIdentities));
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
        var exception = Assert.Throws<InvalidDataException>(() => VerifyArchive(
            archive,
            package,
            inventory.ReleaseVersion,
            inventory.Packages.Select(static item => item.PackageId).ToHashSet(StringComparer.Ordinal),
            inventory.ContractIdentities));
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
        var exception = Assert.ThrowsAny<Exception>(() => VerifyArchive(
            archive,
            package,
            inventory.ReleaseVersion,
            inventory.Packages.Select(static item => item.PackageId).ToHashSet(StringComparer.Ordinal),
            inventory.ContractIdentities));
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

        foreach (var pdbPath in paths.Where(static path => path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
        {
            var assemblyPath = $"{pdbPath[..^4]}.dll";
            Assert.Contains(assemblyPath, paths, StringComparer.OrdinalIgnoreCase);
        }

        var payloadBytes = ValidateContentIndex(archive, package.PackageId);
        Assert.InRange(payloadBytes, 1, MaxPayloadBytes);
        using var manifest = ReadJson(archive, ManifestPath, package.PackageId);
        VerifyManifest(
            archive,
            manifest.RootElement,
            package,
            releaseVersion,
            packageIds,
            contractIdentities);
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
        Assert.Equal(3, root.GetProperty("manifestVersion").GetInt32());
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
        Assert.All(runtimeTargets, static target =>
        {
            Assert.Equal("worker", RequiredString(target, "kind"));
            Assert.Equal("net10.0", RequiredString(target, "targetFramework"));
            Assert.Contains(
                "worker-protocol.v3",
                target.GetProperty("requiredHostCapabilities").EnumerateArray()
                    .Select(static capability => capability.GetString()),
                StringComparer.Ordinal);
        });
        Assert.All(appTargets, static target =>
        {
            Assert.Equal("avalonia", RequiredString(target, "kind"));
            Assert.Equal("net10.0", RequiredString(target, "targetFramework"));
            Assert.DoesNotContain(
                "worker-protocol.v3",
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
            .Select(static package => new PackageExpectation(
                RequiredString(package, "packageId"),
                package.TryGetProperty("appProjectPath", out var appProjectPath)
                && !string.IsNullOrWhiteSpace(appProjectPath.GetString()),
                package.GetProperty("providerIds").EnumerateArray()
                    .Select(static providerId => providerId.GetString()!)
                    .Order(StringComparer.Ordinal)
                    .ToArray()))
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
