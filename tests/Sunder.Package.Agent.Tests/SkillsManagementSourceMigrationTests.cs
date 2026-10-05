using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias SkillsWorker;
using SkillsWorker::Sunder.Package.Agent.Skills.Runtime;
using SkillsWorker::Sunder.Package.Agent.Skills.Services;

public sealed class SkillsManagementSourceMigrationTests
{
    internal static readonly string[] SourcePaths =
    [
        "src/Sunder.Package.Agent.Skills/Rpc/ManagementService.cs",
        "src/Sunder.Package.Agent.Skills/Rpc/ManagementClient.cs",
        "src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeOwnerClient.cs",
    ];

    [AgentPublicSourceMigrationFact]
    public async Task ManagementSource_PreservesContractAndRealTransferOperationsAcrossIsolatedApp()
    {
        using var source = PrivateManagementSourceFixture.Load("Skills", RegressionTestRpcDescriptors.Get("sunder.package.agent.skills.owner"));
        using var package = RegressionTestPackageScope.Create();
        var store = new SkillStore(package.Context);
        store.Activate();
        var handler = new SkillRuntimeHandler(store, new SkillImportService(store, new UnusedGitHubClient(), package.Context), package.Context);
        await handler.OrphanedTransferCleanup;
        var operations = new SkillRuntimeOwnerClient(handler, new SkillRuntimeChangeStream(store));
        var type = source.Worker.GetType("Sunder.Package.Agent.Skills.Rpc.ManagementService", true)!;
        var service = AgentRoleBoundary.Create(type, operations);
        var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(service, null));
        Assert.Equal("skills.management", registration.ProviderId);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        state.Add(SkillConstants.PackageId, source.Descriptor, service, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        var rpc = new RegressionTestRpcClient(state, SkillConstants.PackageId);
        var client = AgentRoleBoundary.As<ISkillOwnerClient>(AgentRoleBoundary.Create(source.App.GetType("Sunder.Package.Agent.Skills.Runtime.SkillOwnerClient", true)!, rpc));
        Assert.Empty((await client.QueryAsync()).Skills);
        using var lifetime = new CancellationTokenSource();
        await using var changes = client.SubscribeChangesAsync(lifetime.Token).GetAsyncEnumerator();
        Assert.True(await changes.MoveNextAsync());

        var archive = CreateArchive("SKILL.md", "---\nname: Source Skill\n---\nSource transport import.");
        var transferId = await Upload(archive);
        var next = changes.MoveNextAsync().AsTask();
        var imported = await client.CommandAsync(new(SkillCommandKind.ImportTransfer, transferId));
        var skill = Assert.Single(imported.Skills);
        Assert.Equal("Source Skill", skill.Name);
        Assert.Null(skill.SourceUri);
        Assert.Equal(skill.SkillId, Assert.Single(imported.ImportedSkillIds));
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(skill.SkillId, Assert.Single((await client.QueryAsync()).Skills).SkillId);
        Assert.Null(await package.Context.Storage.Files.ReadAsync($"skill-import-transfers/{transferId}/manifest.json"));
        Assert.Contains("Source transport import.", await File.ReadAllTextAsync(Path.Combine(store.GetSkillRootPath(skill), "SKILL.md")), StringComparison.Ordinal);

        var required = await Assert.ThrowsAsync<SunderRpcException>(() => client.CommandAsync(new(SkillCommandKind.Delete)).AsTask());
        Assert.Equal("skills.owner.operation-failed", required.Error.Code);
        var invalid = await Assert.ThrowsAsync<SunderRpcException>(() => client.CommandAsync(new((SkillCommandKind)999)).AsTask());
        Assert.Equal("skills.owner.operation-failed", invalid.Error.Code);
        var unsafeId = await Upload(CreateArchive("../escape.txt", "not allowed"));
        var unsafePath = await Assert.ThrowsAsync<SunderRpcException>(() => client.CommandAsync(new(SkillCommandKind.ImportTransfer, unsafeId)).AsTask());
        Assert.Equal("skills.owner.operation-failed", unsafePath.Error.Code);
        Assert.Null(await package.Context.Storage.Files.ReadAsync($"skill-import-transfers/{unsafeId}/manifest.json"));
        Assert.Single(store.ListSkills());

        state.FailNextResponseContentRegistration(new IOException("private skills content failure"));
        var fault = await Assert.ThrowsAsync<SunderRpcException>(() => client.QueryAsync().AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, fault.Error.Kind);
        Assert.DoesNotContain("private skills content failure", fault.Message, StringComparison.Ordinal);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CommandAsync(new(SkillCommandKind.Delete, skill.SkillId), cancelled.Token).AsTask());
            Assert.Single(store.ListSkills());
        }
        Assert.Empty((await client.CommandAsync(new(SkillCommandKind.Delete, skill.SkillId))).Skills);
        await lifetime.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => changes.MoveNextAsync().AsTask());
        await state.RetireAsync(service);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.QueryAsync().AsTask());

        async Task<string> Upload(byte[] bytes)
        {
            var id = Guid.NewGuid().ToString("N");
            await package.Context.Storage.Files.WriteAsync($"skill-import-transfers/{id}/chunks/0000.bin", bytes);
            await package.Context.Storage.Files.WriteAsync($"skill-import-transfers/{id}/manifest.json", JsonSerializer.SerializeToUtf8Bytes(
                new SkillTransferManifest(1, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)))));
            return id;
        }
    }

    private static byte[] CreateArchive(string path, string contents)
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        using (var stream = archive.CreateEntry(path).Open())
            stream.Write(Encoding.UTF8.GetBytes(contents));
        return bytes.ToArray();
    }

    private sealed class UnusedGitHubClient : IGitHubSkillClient
    {
        public Task<string?> TryGetDefaultBranchAsync(string owner, string repo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GitHubSkillFolder?> TryGetFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GitHubSkillFolder?> TryGetSkillFolderAsync(GitHubSkillFolderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GitHubSkillFile>> ListFilesAsync(GitHubSkillFolder folder, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> ReadFileAsync(GitHubSkillFolder folder, GitHubSkillFile file, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Noop : IDisposable { internal static Noop Instance { get; } = new(); public void Dispose() { } }
}
