using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    // Exercise the actual authored root. The fixture receives copies of its role
    // outputs for collectible probes, never a reconstructed project/owned baseline.
    private static async Task<CanonicalFamilyBuild> BuildCanonicalFamily(
        string root, string family, string fixtureDirectory, string cache, string? ownerArchive = null)
    {
        var repository = BuilderSourceMigrationTests.FindRepository();
        var name = family == "Agent" ? "Sunder.Package.Agent" : "Sunder.Package.Agent." + family;
        var source = Path.Combine(repository, "src", name);
        var project = Path.Combine(source, name + ".csproj");
        Assert.True(File.Exists(project), $"The canonical {family} package root is required: {project}");
        var authored = XDocument.Load(project);
        Assert.Empty(authored.Descendants("SunderPackageAppProject"));
        Assert.DoesNotContain(authored.Descendants("ProjectReference"), reference =>
            ((string?)reference.Attribute("Include"))?.Replace('\\', '/').Contains("/Protocol/", StringComparison.Ordinal) == true
            || ((string?)reference.Attribute("Include"))?.Contains("OwnerProtocol", StringComparison.Ordinal) == true);

        var catalog = Environment.GetEnvironmentVariable("SUNDER_AGENT_SOURCE_PLATFORM_CATALOG");
        Assert.True(!string.IsNullOrWhiteSpace(catalog) && File.Exists(catalog),
            "Set SUNDER_AGENT_SOURCE_PLATFORM_CATALOG to the canonical local Core catalog selected by the family locks. "
            + "This required-input lane never substitutes Registry or conformance-catalog evidence.");
        var fixture = Directory.CreateDirectory(Path.Combine(root, fixtureDirectory)).FullName;
        var evidence = new[] { "sunder-contracts.lock.json", "sunder-contracts.owned.json" }
            .ToDictionary(file => file, file => file == "sunder-contracts.owned.json"
                ? CanonicalPackageRoles.ReadOwnedBaseline(source) : File.ReadAllBytes(Path.Combine(source, file)), StringComparer.Ordinal);
        bool hasImports;
        using (var selection = JsonDocument.Parse(evidence["sunder-contracts.lock.json"]!))
        {
            AssertLocalSelection(selection.RootElement);
            hasImports = selection.RootElement.GetProperty("packageArtifacts").EnumerateObject().Any()
                || selection.RootElement.GetProperty("platformImports").EnumerateObject().Any();
        }
        var build = new CanonicalFamilyBuild(project, source, fixture, cache, evidence);
        // Imported props participate in the authored configuration. Raw XML is
        // not authoritative for whether the canonical root is a package project.
        using (var evaluation = JsonDocument.Parse(await build.Run("msbuild", denyNetwork: false, arguments:
                   ["-getProperty:SunderPackageProject,SunderPackageFamilyId,OutputType,SunderPackageAppProject,SunderPackageRole,MSBuildProjectFullPath"])))
        {
            var properties = evaluation.RootElement.GetProperty("Properties");
            Assert.Equal("true", properties.GetProperty("SunderPackageProject").GetString(), ignoreCase: true);
            Assert.Equal(AgentPackageRepositoryInventory.GetRuntimePackageId(name), properties.GetProperty("SunderPackageFamilyId").GetString());
            Assert.Equal("Library", properties.GetProperty("OutputType").GetString(), ignoreCase: true);
            Assert.Equal(string.Empty, properties.GetProperty("SunderPackageAppProject").GetString());
            Assert.Equal(string.Empty, properties.GetProperty("SunderPackageRole").GetString());
            Assert.Equal(Path.GetFullPath(project), properties.GetProperty("MSBuildProjectFullPath").GetString());
        }
        // Core's producer closure is a required parent input. Restoring this
        // exact-RID consumer must not rewrite its referenced Core lock files.
        await build.Run("restore", "--disable-parallel", "--no-dependencies");
        if (ownerArchive is not null) await build.SelectFixtureOwner(ownerArchive, catalog!);
        var restore = new List<string> { "-t:RestoreSunderContracts" };
        // An independent local family still exercises restore, but has no platform
        // constraints for an offline catalog to satisfy. Core rejects such an override.
        if (hasImports) restore.Add("-p:SunderPlatformCatalogPath=" + Path.GetFullPath(catalog!));
        if (ownerArchive is not null) restore.Add("-p:SunderPackageCandidatePath=" + ownerArchive);
        await build.Run("msbuild", denyNetwork: true, arguments: restore.ToArray());
        await build.Run("build", "--no-restore");
        build.AssertEvidenceUnchanged();
        build.CopyOutputs();
        return build;
    }

    private static void AssertLocalSelection(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                Assert.NotEqual("registryOrigin", property.Name);
                AssertLocalSelection(property.Value);
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) AssertLocalSelection(item);
    }

    private sealed class CanonicalFamilyBuild(
        string project, string source, string fixture, string cache, IReadOnlyDictionary<string, byte[]?> evidence)
    {
        private string? _fixtureLockPath;
        private byte[]? _fixtureLock;
        internal string Directory => fixture;

        internal Task Run(string command, params string[] arguments) => Run(command, false, arguments);

        internal async Task SelectFixtureOwner(string archive, string catalog)
        {
            // A fresh source artifact may have a different content identity from
            // the production selection. Update a test-local copy through the
            // real authoring command; never manufacture or rewrite product pins.
            using var candidate = File.OpenRead(archive);
            var candidateSha = Convert.ToHexStringLower(SHA256.HashData(candidate));
            // A selected lock is an authored input to qualification. Keep this
            // tool-generated copy inside the reviewed source root, under obj,
            // instead of weakening the source-root fence to include all of /tmp.
            var selection = System.IO.Directory.CreateDirectory(Path.Combine(source, "obj", "source-artifact-selections", candidateSha)).FullName;
            _fixtureLockPath = Path.Combine(selection, "sunder-contracts.lock.json");
            File.WriteAllBytes(_fixtureLockPath, evidence["sunder-contracts.lock.json"]!);
            await Run("msbuild", denyNetwork: true, arguments:
                ["-t:UpdateSunderPackageApi", "-p:SunderPackageId=younics.ai.agent", "-p:SunderPackageCandidatePath=" + archive,
                    "-p:SunderPlatformCatalogPath=" + Path.GetFullPath(catalog)]);
            _fixtureLock = File.ReadAllBytes(_fixtureLockPath);
            using var before = JsonDocument.Parse(evidence["sunder-contracts.lock.json"]!);
            using var after = JsonDocument.Parse(_fixtureLock);
            AssertLocalSelection(after.RootElement);
            foreach (var property in new[] { "packageImports", "providerImports", "platformImports" })
                Assert.True(JsonElement.DeepEquals(before.RootElement.GetProperty(property), after.RootElement.GetProperty(property)), property + " intent changed");
            var oldArtifact = before.RootElement.GetProperty("packageArtifacts").GetProperty("younics.ai.agent");
            var newArtifact = after.RootElement.GetProperty("packageArtifacts").GetProperty("younics.ai.agent");
            foreach (var property in new[] { "requestedVersionRange", "includePrerelease", "selectedPackageVersion", "publicContractCount" })
                Assert.True(JsonElement.DeepEquals(oldArtifact.GetProperty(property), newArtifact.GetProperty(property)), property + " changed");
        }

        internal async Task<string> Run(string command, bool denyNetwork, params string[] arguments)
        {
            var start = new ProcessStartInfo(BuilderSourceMigrationTests.DotnetBuildHost)
            {
                WorkingDirectory = source,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { command, project }.Concat(arguments).Concat([
                "-p:Configuration=Debug", "-p:RuntimeIdentifier=" + RuntimeInformation.RuntimeIdentifier,
                "-p:SunderUseSourceSdk=true", "-p:BuildProjectReferences=false",
                "-p:SunderContractCacheRoot=" + cache, "-nodeReuse:false", "-nologo"]))
                start.ArgumentList.Add(argument);
            var task = Path.GetFullPath(Path.Combine(BuilderSourceMigrationTests.FindRepository(),
                "../sunder-core/languages/csharp/dotnet/packages/Sunder.Package.Build/bin/Debug/net10.0/Sunder.Package.Build.dll"));
            var stamp = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(task)));
            start.ArgumentList.Add("-p:SunderPackageBuildTaskSourceIsImmutable=true");
            start.ArgumentList.Add("-p:SunderPackageBuildTaskAssembly=" + task);
            start.ArgumentList.Add("-p:SunderPackageBuildTaskShadowRoot=" + Path.Combine(Path.GetTempPath(), "sunder-agent-artifact-task-shadows"));
            start.ArgumentList.Add("-p:SunderPackageBuildTaskShadowStamp=" + stamp);
            start.ArgumentList.Add("-p:SunderPackageRoleInputSourceRoot=" + Path.Combine(BuilderSourceMigrationTests.FindRepository(), "src"));
            if (_fixtureLockPath is not null) start.ArgumentList.Add("-p:SunderContractLockPath=" + _fixtureLockPath);
            if (denyNetwork)
            {
                // NuGet restore above may acquire ordinary SDK dependencies. The
                // local contract-evidence operation must have no network fallback.
                foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
                    start.Environment[key] = "http://127.0.0.1:1";
                start.Environment["NO_PROXY"] = start.Environment["no_proxy"] = "";
            }
            var commandEvidence = BeginCanonicalCommandEvidence(source, command, start.ArgumentList, _fixtureLockPath);
            if (commandEvidence is not null) start.ArgumentList.Add("-bl:" + commandEvidence + ".binlog");
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(900));
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                EndCanonicalCommandEvidence(commandEvidence, source, _fixtureLockPath, process.ExitCode, await output, await error);
                throw;
            }
            var stdout = await output;
            var stderr = await error;
            EndCanonicalCommandEvidence(commandEvidence, source, _fixtureLockPath, process.ExitCode, stdout, stderr);
            Assert.True(process.ExitCode == 0, $"Canonical command failed: {string.Join(' ', start.ArgumentList)}\n{stdout}\n{stderr}");
            AssertEvidenceUnchanged();
            return stdout;
        }

        internal async Task Pack()
        {
            System.IO.Directory.CreateDirectory(Path.Combine(fixture, "bin"));
            await Run("msbuild", "-t:PackSunderPackage", "-p:SunderPackageOutputPath=" + Path.Combine(fixture, "bin/agent-source.sunderpkg"));
            CopyOutputs();
        }

        internal void AssertEvidenceUnchanged()
        {
            foreach (var (file, bytes) in evidence)
                Assert.Equal(bytes, file == "sunder-contracts.owned.json" ? CanonicalPackageRoles.ReadOwnedBaseline(source) : File.ReadAllBytes(Path.Combine(source, file)));
            if (_fixtureLock is not null) Assert.Equal(_fixtureLock, File.ReadAllBytes(_fixtureLockPath!));
        }

        internal void CopyOutputs()
        {
            CopyDirectory(Path.Combine(source, "bin/Debug/net10.0"), Path.Combine(fixture, "bin/Debug/net10.0"));
            CopyDirectory(Path.Combine(source, "obj/Debug/net10.0/sunder-analysis"), Path.Combine(fixture, "obj/Debug/net10.0/sunder-analysis"));
            foreach (var (file, bytes) in evidence)
                if (bytes is not null)
                    File.WriteAllBytes(Path.Combine(fixture, file), file == "sunder-contracts.lock.json" ? _fixtureLock ?? bytes : bytes);
                else Assert.False(File.Exists(Path.Combine(fixture, file)), "An absent owned baseline must remain absent in the fixture.");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Assert.True(Directory.Exists(source), $"Required generated output is missing: {source}");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var child in Directory.EnumerateDirectories(source)) CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
    }

    private static async Task ProjectCanonicalWorker(string worker, string driver)
    {
        // The projection driver remains wholly inside the temporary test fixture.
        // Production projects contain only their ordinary authoring/build targets.
        var project = new XElement("Project",
            new XElement("UsingTask", new XAttribute("TaskName", "Sunder.Package.Build.Tests.AgentSourceArtifactTestTask"), new XAttribute("AssemblyFile", driver)),
            new XElement("Target", new XAttribute("Name", "ProjectWorker"),
                new XElement("AgentSourceArtifactTestTask", new XAttribute("Operation", "Project"),
                    new XAttribute("ProjectDirectory", worker), new XAttribute("Candidate", Path.Combine(worker, "bin/agent-source.sunderpkg")),
                    new XAttribute("ProjectionPath", Path.Combine(worker, "bin/worker-projection")),
                    new XAttribute("RuntimeIdentifier", RuntimeInformation.RuntimeIdentifier))));
        new XDocument(project).Save(Path.Combine(worker, "ProjectWorker.proj"));
        await Run(worker, "msbuild", "ProjectWorker.proj", "-t:ProjectWorker");
    }
}
