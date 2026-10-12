using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentOwnerSourceMigrationTests
{
    private static async Task VerifyStackEvidence(string sourceRoot, JsonElement receipt)
    {
        var platform = Assert.Single(receipt.GetProperty("platformImports").EnumerateArray());
        Assert.Equal("sunder.core", platform.GetProperty("authorityId").GetString());
        Assert.Equal("1.1.0", platform.GetProperty("selectedAuthorityRelease").GetString());
        Assert.Equal(new[] { "agent.stack.profiles", "agent.stack.workspaces" }, platform.GetProperty("providerRefs").EnumerateArray().Select(value => value.GetProperty("providerId").GetString()));
        var bundle = Assert.Single(receipt.GetProperty("bundles").EnumerateArray(), value => value.GetProperty("source").GetString() == "imported");
        Assert.Equal("sunder.stack.contributor", bundle.GetProperty("contractKey").GetString());
        Assert.Equal("platform", bundle.GetProperty("authorityKind").GetString());
        var descriptor = Assert.Single(receipt.GetProperty("contracts").EnumerateArray(), value => value.GetProperty("contractId").GetString() == "sunder.stack.contributor");
        Assert.Equal("38c8f49840f951aec29398d3641ff2306c906aee12ea80c2277db734bce49898", descriptor.GetProperty("descriptorSha256").GetString());
        Assert.Equal("6ca6d6385c720290548592de890f3e3ceb86f3d999167f4e80c6afb15f8a02d8", descriptor.GetProperty("abiSha256").GetString());
        Assert.Empty(receipt.GetProperty("packageArtifacts").EnumerateArray());
        Assert.Empty(receipt.GetProperty("packageArtifactImports").EnumerateArray());
        using var export = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CanonicalAgentRoles.AnalysisRoot, "package-export.v1.json")));
        Assert.Equal("2.0.0", export.RootElement.GetProperty("packageVersion").GetString());
        Assert.Equal(21, export.RootElement.GetProperty("contracts").GetArrayLength());
        Assert.DoesNotContain(export.RootElement.GetProperty("contracts").EnumerateArray(), value => value.GetProperty("contractId").GetString() == "sunder.stack.contributor");

        var catalog = Environment.GetEnvironmentVariable("SUNDER_AGENT_SOURCE_PLATFORM_CATALOG");
        Assert.True(!string.IsNullOrWhiteSpace(catalog) && File.Exists(catalog), "Supply the exact canonical local Core catalog for the required input-corruption lane.");
        var catalogBytes = File.ReadAllBytes(catalog!);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(catalogBytes)), platform.GetProperty("selectedAuthorityContentIdentity").GetString());
        var repository = RegressionTestRpcDescriptors.RepositoryRoot;
        var core = Path.GetFullPath(Path.Combine(repository, "../sunder-core"));
        var sandbox = Directory.CreateTempSubdirectory("sunder-agent-canonical-inputs-").FullName;
        // Use one physical identity for the cloned project and its resource
        // inputs; macOS exposes the temporary directory through /var as well.
        if (OperatingSystem.IsMacOS() && sandbox.StartsWith("/var/", StringComparison.Ordinal)) sandbox = "/private" + sandbox;
        try
        {
            // An unchanged copy of the authored repository topology, not a test
            // project reconstructed from legacy Compile items. Mutations stay here.
            foreach (var file in Directory.EnumerateFiles(repository))
                if (Path.GetExtension(file) is ".props" or ".targets" or ".config" || Path.GetFileName(file) is "global.json" or ".editorconfig")
                    File.Copy(file, Path.Combine(sandbox, Path.GetFileName(file)));
            CopySources(Path.Combine(repository, "src"), Path.Combine(sandbox, "src"));
            var projectRoot = Path.Combine(sandbox, "src/Sunder.Package.Agent");
            var project = Path.Combine(projectRoot, "Sunder.Package.Agent.csproj");
            Assert.Equal(File.ReadAllBytes(Path.Combine(sourceRoot, "Sunder.Package.Agent.csproj")), File.ReadAllBytes(project));
            var cache = Path.Combine(sandbox, "cache");
            var lockPath = Path.Combine(projectRoot, "sunder-contracts.lock.json");
            var baselinePath = Path.Combine(projectRoot, "sunder-contracts.owned.json");
            var locked = File.ReadAllBytes(lockPath);
            var baseline = File.ReadAllBytes(baselinePath);
            var taskAssembly = Path.Combine(core, "languages/csharp/dotnet/packages/Sunder.Package.Build/bin/Debug/net10.0/Sunder.Package.Build.dll");
            var common = new[]
            {
                "-p:Configuration=Debug", "-p:RuntimeIdentifier=" + RuntimeInformation.RuntimeIdentifier,
                "-p:SunderContractCacheRoot=" + cache, "-p:SunderUseSourceSdk=true", "-p:BuildProjectReferences=false",
                "-p:SunderSdkProject=" + Sdk("Sunder.Sdk"), "-p:SunderSdkWorkerProject=" + Sdk("Sunder.Sdk.Worker"),
                "-p:SunderSdkAvaloniaProject=" + Sdk("Sunder.Sdk.Avalonia"), "-p:SunderSdkStacksProject=" + Sdk("Sunder.Sdk.Stacks"),
                "-p:SunderPackageBuildTargets=" + Path.Combine(core, "languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Package.Build.targets"),
                "-p:SunderPackageBuildTaskAssembly=" + taskAssembly,
                "-p:SunderPackageBuildTaskSourceIsImmutable=true",
                "-p:SunderPackageBuildTaskShadowRoot=" + Path.Combine(sandbox, "artifacts/task-shadows"),
                "-p:SunderPackageBuildTaskShadowStamp=" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(taskAssembly))),
                "-nodeReuse:false", "-nologo",
            };
            await Command("restore", ["--disable-parallel"]);
            await Command("msbuild", ["-t:RestoreSunderContracts", "-p:SunderPlatformCatalogPath=" + Path.GetFullPath(catalog!)]);
            await Command("build", ["--no-restore"]);
            var generatedRoot = Path.Combine(projectRoot, "obj/Debug/net10.0/sunder-analysis", RuntimeInformation.RuntimeIdentifier, "sunder-rpc");
            var generated = Snapshot();
            var cachedCatalog = Path.Combine(cache, "platform-catalogs", platform.GetProperty("selectedAuthorityContentIdentity").GetString() + ".json");
            var original = File.ReadAllBytes(cachedCatalog);
            var corrupt = original.ToArray(); corrupt[0] ^= 1;
            File.WriteAllBytes(cachedCatalog, corrupt);
            try { await Reject("contract-cache.entry-corrupt"); }
            finally { File.WriteAllBytes(cachedCatalog, original); }
            var missing = JsonNode.Parse(locked)!;
            missing["platformImports"] = new JsonObject();
            File.WriteAllText(lockPath, missing.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            try { await Reject("platform-api.import-not-found"); }
            finally { File.WriteAllBytes(lockPath, locked); }
            await Command("build", ["--no-restore"]);
            Assert.Equal(generated, Snapshot());
            Assert.Equal(locked, File.ReadAllBytes(lockPath));
            Assert.Equal(baseline, File.ReadAllBytes(baselinePath));

            string Sdk(string name) => Path.Combine(core, "languages/csharp/dotnet/packages", name, name + ".csproj");
            string[] Snapshot() => Directory.GetFiles(generatedRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(path => Path.GetRelativePath(projectRoot, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
            async Task Reject(string expected)
            {
                await Command("build", ["--no-restore"], expected);
                Assert.Equal(generated, Snapshot());
                Assert.Equal(baseline, File.ReadAllBytes(baselinePath));
            }
            async Task Command(string command, string[] arguments, string? expectedError = null)
            {
                var start = new ProcessStartInfo("dotnet") { WorkingDirectory = projectRoot, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { command, project }.Concat(arguments).Concat(common)) start.ArgumentList.Add(argument);
                if (command == "msbuild")
                {
                    foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" }) start.Environment[key] = "http://127.0.0.1:1";
                    start.Environment["NO_PROXY"] = start.Environment["no_proxy"] = "";
                }
                using var process = Process.Start(start)!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(900));
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                try { await process.WaitForExitAsync(timeout.Token); }
                catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
                var text = await output + await error;
                if (expectedError is null) Assert.True(process.ExitCode == 0, text);
                else { Assert.NotEqual(0, process.ExitCode); Assert.Contains(expectedError, text, StringComparison.OrdinalIgnoreCase); }
            }
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }

    private static void CopySources(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            if (Path.GetFileName(directory) is not ("bin" or "obj" or ".git")) CopySources(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
