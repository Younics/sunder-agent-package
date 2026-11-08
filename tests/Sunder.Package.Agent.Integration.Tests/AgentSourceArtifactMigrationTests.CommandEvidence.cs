using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static int _artifactCommandSequence;

    private static string? BeginCanonicalCommandEvidence(string source, string command,
        IEnumerable<string> arguments, string? selectedLock)
    {
        var root = Environment.GetEnvironmentVariable("SUNDER_AGENT_SOURCE_ARTIFACT_COMMAND_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return null;
        Directory.CreateDirectory(root);
        var stem = Path.Combine(root, $"{Interlocked.Increment(ref _artifactCommandSequence):D4}-{Path.GetFileName(source)}-{command}");
        WriteArtifactEvidence(stem + ".before.json", new
        {
            source, command, arguments = arguments.ToArray(), selectedLock,
            outputs = CanonicalEvidenceOutputs(source, selectedLock),
        });
        return stem;
    }

    private static void EndCanonicalCommandEvidence(string? stem, string source, string? selectedLock,
        int exitCode, string stdout, string stderr)
    {
        if (stem is null) return;
        File.WriteAllText(stem + ".stdout.log", stdout);
        File.WriteAllText(stem + ".stderr.log", stderr);
        WriteArtifactEvidence(stem + ".after.json", new { exitCode, outputs = CanonicalEvidenceOutputs(source, selectedLock) });
    }

    private static IReadOnlyDictionary<string, string?> CanonicalEvidenceOutputs(string source, string? selectedLock)
    {
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var name = Path.GetFileName(source);
        var paths = new List<string>
        {
            Path.Combine(source, "sunder-contracts.lock.json"), Path.Combine(source, "sunder-contracts.owned.json"),
            Path.Combine(source, "bin/Debug/net10.0/sunder-targets", rid, "role-inputs.v1.json"),
            Path.Combine(source, "bin/Debug/net10.0/sunder-targets", rid, "sunder-dev/manifest/sunder-package.json"),
            Path.Combine(source, "obj/Debug/net10.0/sunder-analysis", rid, "sunder-rpc/authoring-receipt.v1.json"),
            Path.Combine(source, "bin/Debug/net10.0/sunder-roles/worker", rid, name + ".dll"),
            Path.Combine(source, "bin/Debug/net10.0/sunder-roles/app", rid, name + ".App.dll"),
        };
        if (selectedLock is not null) paths.Add(selectedLock);
        return paths.ToDictionary(path => path, path => File.Exists(path)
            ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : null, StringComparer.Ordinal);
    }

    private static void RetainArtifactAttempt(string root, bool completed)
    {
        var destination = Environment.GetEnvironmentVariable("SUNDER_AGENT_SOURCE_ARTIFACT_COMMAND_EVIDENCE");
        if (string.IsNullOrWhiteSpace(destination)) return;
        var archives = Path.Combine(destination, "attempt-archives");
        Directory.CreateDirectory(archives);
        var records = new List<object>();
        foreach (var path in Directory.EnumerateFiles(root, "agent-source.sunderpkg", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var archive = ZipFile.OpenRead(path);
            using var stream = archive.GetEntry("manifest/sunder-package.json")!.Open();
            using var manifest = JsonDocument.Parse(stream);
            var id = manifest.RootElement.GetProperty("id").GetString()!;
            var target = Path.Combine(archives, $"{records.Count:D2}-{id}.sunderpkg");
            File.Copy(path, target, overwrite: false);
            records.Add(new { packageId = id, originalPath = path, retainedPath = target,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(target))) });
        }
        WriteArtifactEvidence(Path.Combine(destination, "attempt-artifacts.json"), new { completed, archives = records });
    }

    private static void WriteArtifactEvidence(string path, object value)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(output, value, new JsonSerializerOptions { WriteIndented = true });
    }
}
