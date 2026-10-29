extern alias BuilderApp;

using System.Security.Cryptography;
using System.Text.Json;
using App = BuilderApp::Sunder.Package.Agent.Builder;

namespace Sunder.Package.Agent.Tests;

internal static class BuilderTestDevelopmentOutput
{
    internal static async Task<string> WriteAsync(App.BuilderProjectInspection project, string folder)
    {
        var rid = App.BuilderToolchainPlatform.Current.DotnetRid;
        Directory.CreateDirectory(Path.Combine(folder, "manifest"));
        var payload = Path.Combine(folder, "payload", "runtime", rid);
        Directory.CreateDirectory(payload);
        await File.WriteAllTextAsync(Path.Combine(payload, "worker.js"), "// inert output fixture, never executed\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest", "sunder-package.json"), JsonSerializer.Serialize(new
        {
            archiveFormatVersion = 1, manifestVersion = 1, id = project.PackageId, version = "1.0.0",
            targets = new[] { new { role = "runtime", rid, kind = "worker", entryPoint = "worker.js",
                targetFramework = project.TargetFramework ?? (project.Kind == App.BuilderProjectKind.Dotnet ? "net10.0" : "node24") } },
        }));
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Where(path => Path.GetFileName(path) != "content-index.json")
            .Select(path => new { path = Path.GetRelativePath(folder, path).Replace('\\', '/'), size = new FileInfo(path).Length,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest", "content-index.json"), JsonSerializer.Serialize(new { schemaVersion = 1, files }));
        return await App.BuilderProjectDriverOutputs.ValidateDevelopmentAsync(folder, project, rid, false, default);
    }
}
