extern alias SkillsWorker;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SkillsWorker::Sunder.Package.Agent.Skills.Runtime;
using Sunder.Package.Agent.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    [UnprivilegedUnixTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Capability", "Process")]
    public async Task CommonSkillImport_AccessDeniedIsADomainErrorAndEarlierRootsRemainQueryable(bool partial)
    {
        var root = Directory.CreateTempSubdirectory("sunder-skills-rpc-").FullName;
        var denied = Path.Combine(root, "home", partial ? ".config/opencode/skills/denied" : ".agents/skills/denied", "SKILL.md");
        try
        {
            if (partial) WriteSkill(Path.Combine(root, "home/.agents/skills/first"), "first");
            WriteSkill(Path.GetDirectoryName(denied)!, "denied");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(denied, UnixFileMode.None);
            await using var worker = CreateSkillsImportWorker(root);
            await worker.Prepare();
            await worker.Activate();
            var error = await worker.InvokeError("skills.management", "skills-management", "import-common", new { });
            Assert.Equal("domain", error.GetProperty("kind").GetString());
            Assert.Equal("skills.owner.operation-failed", error.GetProperty("code").GetString());
            Assert.Contains(partial ? "Imported 1 skill(s) before the failure." : "Could not discover skills", error.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Empty(worker.ProviderFaults);

            await worker.Invoke("skills.management", "skills-management", "query", new { });
            var skills = Assert.Single(worker.RegisteredContent).GetProperty("skills").EnumerateArray().ToArray();
            Assert.Equal(partial ? 1 : 0, skills.Length);
            if (partial) Assert.Equal("first", skills[0].GetProperty("skillId").GetString());
            Assert.False(File.Exists(Path.Combine(root, "workspace/skills/skills.json.import-journal")));
            Assert.Empty(Directory.Exists(Path.Combine(root, "workspace/skill-import"))
                ? Directory.GetFileSystemEntries(Path.Combine(root, "workspace/skill-import")) : []);
            await worker.Shutdown();
            var log = Assert.Single(worker.PackageLogs, log => log.GetProperty("attributes").TryGetProperty("ImportPhase", out _))
                .GetProperty("attributes");
            Assert.Equal("System.UnauthorizedAccessException", log.GetProperty("ExceptionType").GetString());
            Assert.Equal(13, log.GetProperty("NativeError").GetInt32());
            Assert.Equal(partial ? 1 : 0, log.GetProperty("CompletedCount").GetInt32());
            Assert.Contains("denied/SKILL.md", log.GetProperty("FailureMessage").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            if (!OperatingSystem.IsWindows() && File.Exists(denied))
                File.SetUnixFileMode(denied, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(root, recursive: true);
        }
    }

    [UnixFact("A launchd-like umask and Unix creation permissions require Unix.")]
    [Trait("Capability", "Process")]
    public async Task CommonSkillImport_RealWorkerCopiesAndReopensFilesWithPrivatePermissions()
    {
        var root = Directory.CreateTempSubdirectory("sunder-skills-rpc-").FullName;
        try
        {
            var source = Path.Combine(root, "home/.agents/skills/first");
            WriteSkill(source, "first");
            Directory.CreateDirectory(Path.Combine(source, "references"));
            await File.WriteAllTextAsync(Path.Combine(source, "references/guide.md"), "private reference");
            await using var worker = CreateSkillsImportWorker(root);
            await worker.Prepare();
            await worker.Activate();
            await worker.Invoke("skills.management", "skills-management", "import-common", new { });
            var projection = Assert.Single(worker.RegisteredContent);
            Assert.Equal("first", Assert.Single(projection.GetProperty("importedSkillIds").EnumerateArray()).GetString());
            var installed = Path.Combine(root, "workspace/skills/first");
            foreach (var file in Directory.EnumerateFiles(installed, "*", SearchOption.AllDirectories))
            {
                if (!OperatingSystem.IsWindows())
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
                Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(source, Path.GetRelativePath(installed, file))), await File.ReadAllBytesAsync(file));
            }
            await worker.Invoke("skills.management", "skills-management", "query", new { });
            Assert.Equal(2, worker.RegisteredContent.Count);
            Assert.Empty(worker.ProviderFaults);
            await worker.Shutdown();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [UnixTheory("This fixture launches the Unix Worker under an isolated umask.")]
    [InlineData("skills.json")]
    [InlineData("skills.json.import-journal")]
    [Trait("Capability", "Process")]
    public async Task CommonSkillImport_CorruptPersistentStateIsStillAProviderFault(string fileName)
    {
        var root = Directory.CreateTempSubdirectory("sunder-skills-rpc-").FullName;
        try
        {
            WriteSkill(Path.Combine(root, "home/.agents/skills/first"), "first");
            await using var worker = CreateSkillsImportWorker(root);
            await worker.Prepare();
            await worker.Activate();
            var path = Path.Combine(root, "workspace/skills", fileName);
            await File.WriteAllTextAsync(path, "{broken");
            var error = await worker.InvokeError("skills.management", "skills-management", "import-common", new { });
            Assert.Equal("provider-fault", error.GetProperty("kind").GetString());
            Assert.Equal("rpc.provider.handler-fault", error.GetProperty("code").GetString());
            Assert.Single(worker.ProviderFaults);
            Assert.Equal("{broken", await File.ReadAllTextAsync(path));
            await worker.Shutdown();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static PackageWorkerPeer CreateSkillsImportWorker(string root)
    {
        var repository = BuilderSourceMigrationTests.FindRepository();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dev = Path.Combine(repository, "src/Sunder.Package.Agent.Skills/bin", configuration, "net10.0/sunder-dev");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dev, "manifest/sunder-package.json")));
        var payload = Path.Combine(root, "payload");
        CopyDirectory(Path.Combine(dev, "payload/runtime/shared"), payload);
        CopyDirectory(Path.Combine(dev, "payload/runtime", RuntimeInformation.RuntimeIdentifier), payload);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(typeof(SkillRuntimeHandler).Assembly.Location)),
            SHA256.HashData(File.ReadAllBytes(Path.Combine(payload, "lib/Sunder.Package.Agent.Skills.dll"))));
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
        return new PackageWorkerPeer(root, payload, workspace, manifest.RootElement, configureStart: start =>
        {
            var module = start.ArgumentList.Single();
            var host = Environment.ProcessPath is { } executable && Path.GetFileNameWithoutExtension(executable) == "dotnet"
                ? executable : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            start.FileName = "/bin/sh";
            start.ArgumentList.Clear();
            foreach (var argument in new[] { "-c", "umask 077; exec \"$@\"", "sunder-skills-test", host, module }) start.ArgumentList.Add(argument);
            start.Environment["HOME"] = home;
            start.Environment["USERPROFILE"] = home;
        });
    }

    private static void WriteSkill(string path, string name)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "SKILL.md"), $"---\nname: {name}\n---\n# Isolated fixture\n");
    }

    private sealed class UnprivilegedUnixTheoryAttribute : TheoryAttribute
    {
        public UnprivilegedUnixTheoryAttribute()
        {
            if (OperatingSystem.IsWindows()) Skip = "This permission-denied fixture requires Unix.";
            else if (GetEffectiveUserId() == 0)
            {
                const string reason = "A real permission-denied fixture requires a non-root Unix user.";
                TestPrerequisites.ThrowIfRequired(reason);
                Skip = reason;
            }
        }

        [DllImport("libc", EntryPoint = "geteuid")]
        private static extern uint GetEffectiveUserId();
    }
}
