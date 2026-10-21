using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static void VerifyFamilyPolicies(string root, string repository, string core)
    {
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repository, "packages.json")));
        var actualPackages = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "agent-source.sunderpkg", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var archive = ZipFile.OpenRead(path);
            using var stream = archive.GetEntry("manifest/sunder-package.json")!.Open();
            using var manifest = JsonDocument.Parse(stream);
            actualPackages.Add(manifest.RootElement.GetProperty("id").GetString()!);
            CurrentPackagePolicyAssertions.AssertManifest(manifest.RootElement, descriptorPath =>
            {
                var entry = archive.GetEntry("payload/shared/" + descriptorPath);
                Assert.NotNull(entry);
                using var source = entry.Open();
                using var content = new MemoryStream();
                source.CopyTo(content);
                return content.ToArray();
            }, inventory.RootElement);
        }
        Assert.Equal(CurrentContractInventory.Packages.Select(value => value.PackageId).Order(StringComparer.Ordinal), actualPackages.Order(StringComparer.Ordinal));
    }
}
