using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyRejectedImports(string root, string consumer, string archive, string cache)
    {
        var lockPath = Path.Combine(consumer, "sunder-contracts.lock.json");
        var before = File.ReadAllBytes(lockPath);
        var generated = Snapshot();
        foreach (var contract in new[] { "sunder.agent.owner.runtime", "sunder.stack.contributor" })
            await Reject("package-api.provider-contract-not-public", "-t:FixtureOperation", "-p:Operation=Require", "-p:ContractId=" + contract);

        var corrupt = Path.Combine(root, "corrupt.sunderpkg");
        File.Copy(archive, corrupt);
        using (var zip = ZipFile.Open(corrupt, ZipArchiveMode.Update))
        {
            var entry = zip.Entries.First(value => value.FullName.StartsWith("payload/shared/contracts/", StringComparison.Ordinal));
            using var stream = entry.Open();
            stream.Position = stream.Length;
            stream.WriteByte((byte)' ');
        }
        await Reject("package-api.public-inventory-invalid", "-t:FixtureOperation", "-p:Operation=Add", "-p:Candidate=" + corrupt);

        using var locked = JsonDocument.Parse(before);
        var hash = locked.RootElement.GetProperty("packageArtifacts").GetProperty("sunder.package.agent").GetProperty("contracts")
            .GetProperty("sunder.stack.contributor").GetProperty("selectedAuthorityContentIdentity").GetString()!;
        var catalog = Path.Combine(cache, "platform-catalogs", hash + ".json");
        var held = Path.Combine(root, "held-catalog.json");
        File.Move(catalog, held);
        try { await Reject("contract-cache.entry-missing", "-t:FixtureOperation", "-p:Operation=Generate"); }
        finally { File.Move(held, catalog); }
        await Run(consumer, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Check");
        Assert.Equal(before, File.ReadAllBytes(lockPath));
        Assert.Equal(generated, Snapshot());

        string[] Snapshot() => Directory.GetFiles(Path.Combine(consumer, "obj/Debug/net10.0/sunder-rpc"), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(consumer, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        async Task Reject(string expected, params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = consumer, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "msbuild", "Imported.csproj", "-nodeReuse:false", "-v:minimal" }.Concat(arguments)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            var text = await output + await error;
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains(expected, text, StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllBytes(lockPath));
            Assert.Equal(generated, Snapshot());
        }
    }
}
