#if !BUILDER_METADATA_STANDALONE
extern alias BuilderWorker;
using BuilderWorker::Sunder.Package.Agent.Builder;
#else
using Sunder.Package.Agent.Builder;
#endif
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class BuilderProjectMetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sunder-builder-metadata-tests", Guid.NewGuid().ToString("N"));
    private readonly BuilderProjectDiscovery _discovery = new();
    private readonly BuilderProjectMetadataService _metadata = new();

    [Fact]
    public async Task Discovery_ReturnsCanonicalRootsWithoutSdkOrSuffixHeuristics_AndExcludesOutput()
    {
        var first = await DotnetAsync("one", "Arbitrary.App.csproj");
        var second = await NodeAsync("nested/two");
        await WriteAsync("plain/Sunder.Package.Fake.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await DotnetAsync("obj/generated"); await NodeAsync("node_modules/dependency");
        await WriteAsync("npm/package.json", "{\"name\":\"not-a-sunder-project\"}");
        var result = await _discovery.DiscoverAsync(_root);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.EntryPoint == first && item.Kind == BuilderProjectKind.Dotnet);
        Assert.Contains(result, item => item.EntryPoint == second && item.Kind == BuilderProjectKind.Node);
        Assert.All(result, item => Assert.Equal("Old display", item.DisplayName));
    }

    [Fact]
    public async Task Discovery_DoesNotFollowLinkedProjectsOrMetadata()
    {
        await DotnetAsync("actual"); Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), Path.Combine(_root, "actual"));
        Assert.Single(await _discovery.DiscoverAsync(_root));
        await Assert.ThrowsAsync<InvalidDataException>(() => _discovery.InspectAsync(Path.Combine(_root, "linked", "Example.csproj")));
        var other = Path.Combine(_root, "actual", "Extra.cs");
        File.CreateSymbolicLink(other, Path.Combine(_root, "actual", "PackageMetadata.cs"));
        var inspection = await _discovery.InspectAsync(Path.Combine(_root, "actual", "Example.csproj"));
        Assert.False(inspection.CanEditDisplayName); Assert.Contains("Linked source", inspection.ReadOnlyReason);
    }

    [Fact]
    public async Task Dotnet_PrivateBuiltStarter_ChangesOnlySyntaxOwnedIdentityAndMirroredLabels()
    {
        var project = await DotnetAsync("project");
        await WriteAsync("project/obj/Release/sunder-rpc/package-export.v1.json", Receipt("example.old", "package", export: true));
        await WriteAsync("project/Unrelated.cs", "namespace Original.Clr; // example.old must remain in comments\npublic class Other { public string Label => \"Old display\"; }\n");
        var before = await _discovery.InspectAsync(project);
        Assert.True(before.CanEditPackageId, before.PackageIdReadOnlyReason);
        var after = await _metadata.SaveAsync(before, "New \"display\" & title", "example.new");
        Assert.Equal("example.new", after.PackageId); Assert.Equal("New \"display\" & title", after.DisplayName);
        Assert.True(after.CanEditPackageId, after.PackageIdReadOnlyReason); // old private .NET build frames stay intact, not adopted/relabelled
        var view = await File.ReadAllTextAsync(Path.Combine(after.ProjectFolder, "Views/DefaultPackageView.axaml.cs"));
        Assert.Contains("example.new.default", view); Assert.Contains("namespace Original.Clr.Views", view);
        var model = await File.ReadAllTextAsync(Path.Combine(after.ProjectFolder, "Views/DefaultPackageViewModel.cs"));
        Assert.Contains("Sunder.Generated.Packages.Example_New_Services", model);
        Assert.Contains("Sunder.Generated.Packages.Foreign_Package_Services", model);
        Assert.Contains("Old display", await File.ReadAllTextAsync(Path.Combine(after.ProjectFolder, "Unrelated.cs")));
        Assert.Contains("New", await File.ReadAllTextAsync(Path.Combine(after.ProjectFolder, "Views/DefaultPackageView.axaml")));
        Assert.Equal(Receipt("example.old", "package", export: true), await File.ReadAllTextAsync(Path.Combine(after.ProjectFolder, "obj/Release/sunder-rpc/package-export.v1.json")));
    }

    [Theory]
    [InlineData("sunder-contracts.owned.json", "{\"history\":\"retain exact bytes\"}")]
    [InlineData("history/sunder-identity-fork.receipt.json", "retained")]
    [InlineData("obj/sunder-rpc/owned-baseline.sha256", "0123456789abcdef\n")]
    public async Task History_BlocksIdButNameOnlyPreservesExactBaselineAndForeignLocks(string history, string content)
    {
        var project = await DotnetAsync("project");
        await WriteAsync("project/" + history, content);
        const string foreignLock = "{\"lockVersion\":1,\"packageImports\":{\"foreign.owner\":{\"doNotRewrite\":true}}}\n";
        await WriteAsync("project/sunder-contracts.lock.json", foreignLock);
        var before = await _discovery.InspectAsync(project);
        Assert.False(before.CanEditPackageId); Assert.True(before.CanEditDisplayName);
        var original = await File.ReadAllBytesAsync(Path.Combine(before.ProjectFolder, "PackageMetadata.cs"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _metadata.SaveAsync(before, "Changed", "example.new"));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(before.ProjectFolder, "PackageMetadata.cs")));
        var after = await _metadata.SaveAsync(before, "Name only", before.PackageId);
        Assert.Equal("Name only", after.DisplayName);
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(after.ProjectFolder, history)));
        Assert.Equal(foreignLock, await File.ReadAllTextAsync(Path.Combine(after.ProjectFolder, "sunder-contracts.lock.json")));
    }

    [Fact]
    public async Task MissingBaseline_DoesNotErasePublicReceiptHistory()
    {
        var project = await NodeAsync("project");
        await WriteAsync("project/.sunder/generated/rpc/authoring-receipt.v1.json", Receipt("example.old", "public"));
        var inspection = await _discovery.InspectAsync(project);
        Assert.False(inspection.CanEditPackageId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _metadata.SaveAsync(inspection, "Changed", "example.new"));
        Assert.Equal("example.old", (await _discovery.InspectAsync(project)).PackageId);
    }

    [Theory]
    [InlineData("directory-metadata")]
    [InlineData("directory-enumeration")]
    [InlineData("evidence-file")]
    public async Task Inspection_OutputDisappearanceKeepsDiscoveryButNeverAuthorizesMetadataWrites(string boundary)
    {
        var project = await DotnetAsync("project");
        var initial = await _discovery.InspectAsync(project);
        var root = Path.GetDirectoryName(project)!;
        var transient = Path.Combine(root, "obj", "Debug", "sunder-package-compilations.sunder-output.lock.transition");
        Directory.CreateDirectory(transient);
        var evidence = Path.Combine(transient, "package-export.v1.json");
        await File.WriteAllTextAsync(evidence, Receipt("example.old", "public", export: true));
        var removed = false;
        IEnumerable<string> Entries(string directory)
        {
            if (boundary == "directory-enumeration" && directory == transient)
            {
                Directory.Delete(transient, recursive: true);
                removed = true;
                return Directory.EnumerateFileSystemEntries(directory);
            }
            var entries = Directory.GetFileSystemEntries(directory);
            if (boundary == "directory-metadata" && directory == Path.GetDirectoryName(transient))
            {
                Directory.Delete(transient, recursive: true);
                removed = true;
            }
            if (boundary == "evidence-file" && directory == transient)
            {
                File.Delete(evidence);
                removed = true;
            }
            return entries;
        }

        var captured = await BuilderProjectSource.CaptureAsync(root, CancellationToken.None, Entries);
        var inspection = BuilderProjectDiscovery.Inspect(captured, project, BuilderProjectKind.Dotnet);
        Assert.True(removed);
        Assert.Equal(initial.PackageId, inspection.PackageId);
        Assert.Equal(initial.DisplayName, inspection.DisplayName);
        Assert.False(inspection.CanEditDisplayName);
        Assert.False(inspection.CanEditPackageId);
        Assert.Contains("changed during inspection", inspection.ReadOnlyReason);
        // An incomplete empty output tree must not equal a genuinely empty one
        // at the existing final pre-commit source-revision check.
        Assert.NotEqual(initial.SourceRevision, inspection.SourceRevision);
        var metadata = await File.ReadAllBytesAsync(Path.Combine(root, "PackageMetadata.cs"));
        await Assert.ThrowsAsync<BuilderProjectConflictException>(() => _metadata.SaveAsync(inspection, "No mutation", "example.new"));
        Assert.Equal(metadata, await File.ReadAllBytesAsync(Path.Combine(root, "PackageMetadata.cs")));
        Assert.False(File.Exists(Path.Combine(root, ".sunder-builder-metadata.transaction.json")));

        // Generated history is never excluded, even under a lock-looking name.
        Directory.CreateDirectory(transient);
        await File.WriteAllTextAsync(evidence, Receipt("example.old", "public", export: true));
        var stable = await _discovery.InspectAsync(project);
        Assert.True(stable.CanEditDisplayName, stable.ReadOnlyReason);
        Assert.False(stable.CanEditPackageId);
        Assert.Contains("Public or historical", stable.PackageIdReadOnlyReason);
    }

    [Theory]
    [InlineData("obj", "permission")]
    [InlineData("obj", "io")]
    [InlineData("obj", "malformed")]
    [InlineData("obj", "cancelled")]
    [InlineData("", "missing-directory")]
    [InlineData("", "missing-file")]
    public async Task Inspection_OutputChurnDoesNotHidePermissionIntegrityCancellationOrAuthoredSourceFailures(string folder, string failure)
    {
        var project = await DotnetAsync("project");
        var root = Path.GetDirectoryName(project)!;
        var target = Path.Combine(root, folder);
        Directory.CreateDirectory(target);
        Exception expected = failure switch
        {
            "permission" => new UnauthorizedAccessException("permission denied"),
            "io" => new IOException("unrelated I/O failure"),
            "malformed" => new InvalidDataException("invalid source/evidence"),
            "cancelled" => new OperationCanceledException(),
            "missing-directory" => new DirectoryNotFoundException(target),
            _ => new FileNotFoundException("authored source disappeared", target),
        };
        var observed = await Record.ExceptionAsync(() => BuilderProjectSource.CaptureAsync(root, CancellationToken.None,
            directory => directory == target ? throw expected : Directory.EnumerateFileSystemEntries(directory)));
        Assert.Same(expected, observed);
    }

    [Fact]
    public async Task Save_DetectsBothChangedAndNewSourceFiles_BeforeAnyMutation()
    {
        var project = await DotnetAsync("project"); var before = await _discovery.InspectAsync(project);
        await WriteAsync("project/Extra.cs", "// a new compiler input\n");
        await Assert.ThrowsAsync<BuilderProjectConflictException>(() => _metadata.SaveAsync(before, "Changed", before.PackageId));
        before = await _discovery.InspectAsync(project);
        await File.AppendAllTextAsync(Path.Combine(before.ProjectFolder, "PackageMetadata.cs"), "\n// external edit");
        await Assert.ThrowsAsync<BuilderProjectConflictException>(() => _metadata.SaveAsync(before, "Changed", before.PackageId));
        Assert.Equal("Old display", (await _discovery.InspectAsync(project)).DisplayName);
    }

    [Fact]
    public async Task Node_UsesStrictSchemaMetadataAndEditsMirrorsWithoutNpmNameOrUnrelatedFields()
    {
        var project = await NodeAsync("project");
        var inspection = await _discovery.InspectAsync(project);
        Assert.True(inspection.CanEditPackageId, inspection.PackageIdReadOnlyReason);
        var updated = await _metadata.SaveAsync(inspection, "New display & more", "example.new");
        Assert.Equal("New display & more", updated.DisplayName);
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(project));
        Assert.Equal("Keep summary", config.RootElement.GetProperty("summary").GetString());
        Assert.Equal("example.new.main", config.RootElement.GetProperty("app").GetProperty("views")[0].GetProperty("viewId").GetString());
        Assert.Contains("npm-name-is-unrelated", await File.ReadAllTextAsync(Path.Combine(updated.ProjectFolder, "package.json")));
        Assert.Contains("example.new", await File.ReadAllTextAsync(Path.Combine(updated.ProjectFolder, "app/src/App.tsx")));
        Assert.Contains("New display", await File.ReadAllTextAsync(Path.Combine(updated.ProjectFolder, "src/services.ts")));
        Assert.Contains("&amp;", await File.ReadAllTextAsync(Path.Combine(updated.ProjectFolder, "app/index.html")));
        Assert.Equal("{\"lockVersion\":1,\"packageArtifacts\":{},\"packageImports\":{},\"providerImports\":{},\"platformImports\":{}}\n", await File.ReadAllTextAsync(Path.Combine(updated.ProjectFolder, "sunder-contracts.lock.json")));
    }

    [Fact]
    public async Task CustomComputedIdentityAndNonemptyImports_AreReadOnlyForId()
    {
        var project = await NodeAsync("node");
        var source = Path.Combine(Path.GetDirectoryName(project)!, "src/services.ts");
        await File.AppendAllTextAsync(source, "\nexport const custom = true;\n");
        var inspection = await _discovery.InspectAsync(project);
        Assert.False(inspection.CanEditPackageId); Assert.Contains("Computed or custom", inspection.PackageIdReadOnlyReason);
        var fake = await NodeAsync("shadowed");
        var fakeSource = Path.Combine(Path.GetDirectoryName(fake)!, "src/services.ts");
        await File.WriteAllTextAsync(fakeSource, (await File.ReadAllTextAsync(fakeSource)).Replace("import { defineService, type ServiceContext }", "import { type ServiceContext }").Replace("const packageName", "const defineService = customFactory;\nconst packageName"));
        Assert.False((await _discovery.InspectAsync(fake)).CanEditPackageId);
        var dotnet = await DotnetAsync("dotnet");
        await WriteAsync("dotnet/PackageMetadata.cs", "using Sunder.Sdk.Packaging;\n[assembly: SunderPackage(Id = Identity.Id, Name = \"Display\")]\n");
        Assert.False((await _discovery.InspectAsync(dotnet)).CanEditDisplayName);
        project = await NodeAsync("imports");
        await WriteAsync("imports/sunder-contracts.lock.json", "{\"lockVersion\":1,\"packageImports\":{\"foreign\":{}}}");
        Assert.Contains("foreign import lock", (await _discovery.InspectAsync(project)).PackageIdReadOnlyReason);
        var implicitProject = await DotnetAsync("implicit");
        var oldInspection = await _discovery.InspectAsync(implicitProject);
        await WriteAsync("Directory.Build.props", "<Project><Import Project=\"outside.props\" /></Project>");
        Assert.Contains("Implicit build customization", (await _discovery.InspectAsync(implicitProject)).PackageIdReadOnlyReason);
        await Assert.ThrowsAsync<BuilderProjectConflictException>(() => _metadata.SaveAsync(oldInspection, "Name", "example.new"));
    }

    [Fact]
    public async Task JsonDuplicateCaseCollisionsAndEscapingSourcePaths_AreRejected()
    {
        var project = await NodeAsync("node");
        var text = await File.ReadAllTextAsync(project);
        await File.WriteAllTextAsync(project, text.Replace("\"id\":", "\"ID\":\"spoofed\",\"id\":"));
        await Assert.ThrowsAsync<InvalidDataException>(() => _discovery.InspectAsync(project));
        await File.WriteAllTextAsync(project, text.Replace("src/services.ts", "../outside.ts"));
        await Assert.ThrowsAsync<InvalidDataException>(() => _discovery.InspectAsync(project));
    }

    [Fact]
    public async Task InvalidDerivedIdAndInvalidName_DoNotMutateSource()
    {
        var project = await NodeAsync("node"); var inspection = await _discovery.InspectAsync(project);
        await Assert.ThrowsAsync<ArgumentException>(() => _metadata.SaveAsync(inspection, " leading", "example.new"));
        await Assert.ThrowsAsync<ArgumentException>(() => _metadata.SaveAsync(inspection, "Name", new string('a', 120)));
        Assert.Equal(inspection.SourceRevision, (await _discovery.InspectAsync(project)).SourceRevision);
    }

    [Fact]
    public async Task Recovery_RestoresInterruptedMultiFileSaveAndPreservesConcurrentUserEdits()
    {
        var project = await DotnetAsync("project"); var folder = Path.GetDirectoryName(project)!;
        var path = Path.Combine(folder, "PackageMetadata.cs"); var before = await File.ReadAllBytesAsync(path);
        var after = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(before).Replace("Old display", "New display"));
        var journal = JsonSerializer.Serialize(new { schemaVersion = 1, entryPoint = "Example.csproj", files = new[] { new { path = "PackageMetadata.cs", beforeBase64 = Convert.ToBase64String(before), afterBase64 = Convert.ToBase64String(after) } } });
        var journalPath = Path.Combine(folder, ".sunder-builder-metadata.transaction.json");
        await File.WriteAllBytesAsync(path, after); await File.WriteAllTextAsync(journalPath, journal);
        Assert.False((await _discovery.InspectAsync(project)).CanEditDisplayName);
        await _metadata.RecoverAsync(project); Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.False(File.Exists(journalPath));
        await File.WriteAllTextAsync(journalPath, journal); await File.AppendAllTextAsync(path, "\n// user edit\n");
        await Assert.ThrowsAsync<BuilderProjectConflictException>(() => _metadata.RecoverAsync(project));
        Assert.True(File.Exists(journalPath)); Assert.Contains("user edit", await File.ReadAllTextAsync(path));
    }

    private async Task<string> DotnetAsync(string folder, string entry = "Example.csproj")
    {
        await WriteAsync(folder + "/" + entry, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><SunderPackageProject>true</SunderPackageProject></PropertyGroup></Project>");
        await WriteAsync(folder + "/PackageMetadata.cs", "using Sunder.Sdk.Packaging;\n[assembly: SunderPackage(Id = \"example.old\", Name = \"Old display\", Summary = \"Keep summary\")]\n");
        await WriteAsync(folder + "/Services/SampleService.cs", "using Sunder.Sdk.Services;\nnamespace Original.Clr;\n[SunderService] public sealed partial class SampleService { }\n");
        await WriteAsync(folder + "/Views/DefaultPackageView.axaml.cs", "using Sunder.Sdk.Packaging;\nnamespace Original.Clr.Views;\n[SunderPackageView(\"example.old.default\", \"Old display\")] public partial class DefaultPackageView { }\n");
        await WriteAsync(folder + "/Views/DefaultPackageViewModel.cs", "using Sunder.Generated.Packages.Example_Old_Services;\nusing Sunder.Generated.Packages.Foreign_Package_Services;\nnamespace Original.Clr.Views;\npublic sealed class DefaultPackageViewModel;\n");
        await WriteAsync(folder + "/Views/DefaultPackageView.axaml", "<UserControl xmlns=\"https://github.com/avaloniaui\"><TextBlock Text=\"Old display\" FontSize=\"24\" FontWeight=\"SemiBold\" /></UserControl>\n");
        return Path.Combine(_root, folder, entry);
    }
    private async Task<string> NodeAsync(string folder)
    {
        await WriteAsync(folder + "/sunder.package.json", """
            {"schemaVersion":3,"id":"example.old","name":"Old display","summary":"Keep summary","version":"1.0.0","workerCapabilities":[],"services":{"module":"src/services.ts"},"app":{"root":"app","entryPoint":"index.html","views":[{"viewId":"example.old.main","displayName":"Old display","route":"/","defaultPlacement":"middle","showInHotbar":true}]}}
            """);
        await WriteAsync(folder + "/sunder-contracts.lock.json", "{\"lockVersion\":1,\"packageArtifacts\":{},\"packageImports\":{},\"providerImports\":{},\"platformImports\":{}}\n");
        await WriteAsync(folder + "/package.json", "{\"name\":\"npm-name-is-unrelated\"}");
        await WriteAsync(folder + "/src/services.ts", """
            import { defineService, type ServiceContext } from "@sunder/sdk";
            const packageName = "Old display";
            export const messages = defineService({ async send(message: string, context: ServiceContext): Promise<string> { return `${packageName}: ${message}`; } }, { constraints: { "send.message": { maxLength: 256 } } });
            """);
        await WriteAsync(folder + "/app/src/App.tsx", "const packageId = \"example.old\";\nconst packageName = \"Old display\";\nexport function App() { return <><h1>{packageName}</h1><p>Check this package's permissions.</p></>; }\n");
        await WriteAsync(folder + "/app/index.html", "<!doctype html><html><head><title>Old display</title></head><body></body></html>\n");
        return Path.Combine(_root, folder, "sunder.package.json");
    }
    private async Task WriteAsync(string path, string content)
    {
        path = Path.Combine(_root, path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, content);
    }
    private static string Receipt(string id, string exposure, bool export = false) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, packageId = id, packageVersion = "1.0.0",
        producer = new { name = "Sunder.Package.Build" }, inputFingerprint = new string('0', 64), lockSha256 = new string('0', 64),
        contracts = new[] { new { contractKey = "sample", contractVersion = "1.0.0", exposure, source = "owned", canonicalDescriptor = "{}", descriptorBytes = 2,
            descriptorSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("{}"u8)), abiSha256 = new string('0', 64), modelProjection = new { } } },
        bundles = export ? [] : new[] { new { contractKey = "sample", source = "owned" } }
    });
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
