using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias SkillsApp;
extern alias SkillsWorker;
using SkillsWorker::Sunder.Package.Agent.Skills.Runtime;
using SkillsWorker::Sunder.Package.Agent.Skills.Services;
using SkillProjection = SkillsApp::Sunder.Package.Agent.Skills.Runtime.SkillProjection;
using SkillProjectionContent = SkillsApp::Sunder.Package.Agent.Skills.Runtime.SkillProjectionContent;
using SkillsManagementServices = SkillsApp::Sunder.Generated.Packages.Sunder_Package_Agent_Skills_Services;

public sealed class SkillsManagementSourceMigrationTests
{
    internal static readonly string[] SourcePaths =
    [
        "src/Sunder.Package.Agent.Skills/Runtime/SkillRuntimeGateway.cs",
    ];

    [AgentPublicSourceMigrationFact]
    public async Task ManagementSource_PreservesContractAndRealTransferOperationsAcrossIsolatedApp()
    {
        using var package = RegressionTestPackageScope.Create();
        var store = new SkillStore(package.Context);
        store.Activate();
        var handler = new SkillRuntimeHandler(store, new SkillImportService(store, new UnusedGitHubClient(), package.Context), package.Context);
        await handler.OrphanedTransferCleanup;
        var registration = handler.CreateSunderRpcProviderRegistration();
        Assert.Equal("skills.management", registration.ProviderId);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        await using var receiver = new GeneratedServiceTestScope(
            new RegressionTestRpcClient(state, SkillConstants.PackageId));
        state.AddGenerated(SkillConstants.PackageId, handler, registration, Noop.Instance, registration.ProviderId);
        var selected = Assert.Single((await receiver.Providers<SkillsManagementServices.ISkillsManagementService>()
            .ListAsync()).Providers);
        var client = selected.Service;
        Assert.Empty((await ReadProjectionAsync(client.QueryAsync())).Skills);
        using var lifetime = new CancellationTokenSource();
        await using var changes = client.ChangesAsync(lifetime.Token).GetAsyncEnumerator();
        Assert.True(await changes.MoveNextAsync());
        Assert.Equal(0, changes.Current);

        var archive = CreateArchive("SKILL.md", "---\nname: Source Skill\n---\nSource transport import.");
        var transferId = await Upload(archive);
        var next = changes.MoveNextAsync().AsTask();
        var imported = await ReadProjectionAsync(client.ImportTransferAsync(transferId));
        var skill = Assert.Single(imported.Skills);
        Assert.Equal("Source Skill", skill.Name);
        Assert.Null(skill.SourceUri);
        Assert.Equal(skill.SkillId, Assert.Single(imported.ImportedSkillIds));
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, changes.Current);
        Assert.Equal(skill.SkillId, Assert.Single((await ReadProjectionAsync(client.QueryAsync())).Skills).SkillId);
        Assert.Null(await package.Context.Storage.Files.ReadAsync($"skill-import-transfers/{transferId}/manifest.json"));
        var storedSkill = Assert.Single(store.ListSkills(), value => value.SkillId == skill.SkillId);
        Assert.Equal(skill.Name, storedSkill.Name);
        Assert.Contains("Source transport import.", await File.ReadAllTextAsync(Path.Combine(store.GetSkillRootPath(storedSkill), "SKILL.md")), StringComparison.Ordinal);

        var required = await Assert.ThrowsAsync<SunderRpcException>(() => client.DeleteAsync(string.Empty).AsTask());
        Assert.Equal("skills.owner.operation-failed", required.Error.Code);
        var invalid = await Assert.ThrowsAsync<SunderRpcException>(() => client.ImportTransferAsync("invalid").AsTask());
        Assert.Equal("skills.owner.operation-failed", invalid.Error.Code);
        var unsafeId = await Upload(CreateArchive("../escape.txt", "not allowed"));
        var unsafePath = await Assert.ThrowsAsync<SunderRpcException>(() => client.ImportTransferAsync(unsafeId).AsTask());
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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DeleteAsync(skill.SkillId, cancelled.Token).AsTask());
            Assert.Single(store.ListSkills());
        }
        await client.DeleteAsync(skill.SkillId);
        Assert.Empty((await ReadProjectionAsync(client.QueryAsync())).Skills);
        await lifetime.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => changes.MoveNextAsync().AsTask());
        await state.RetireAsync(handler);
        var stale = await Assert.ThrowsAsync<SunderRpcException>(() => selected.EnsureCurrentAsync().AsTask());
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, stale.Error.Kind);
        Assert.True(selected.RetirementToken.IsCancellationRequested);
        var retired = await Assert.ThrowsAsync<SunderRpcException>(() => client.QueryAsync().AsTask());
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, retired.Error.Kind);
        Assert.Equal("rpc.stale-endpoint", retired.Error.Code);

        async Task<string> Upload(byte[] bytes)
        {
            var id = Guid.NewGuid().ToString("N");
            await package.Context.Storage.Files.WriteAsync($"skill-import-transfers/{id}/chunks/0000.bin", bytes);
            await package.Context.Storage.Files.WriteAsync($"skill-import-transfers/{id}/manifest.json", JsonSerializer.SerializeToUtf8Bytes(
                new SkillTransferManifest(1, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)))));
            return id;
        }
    }

    private static ValueTask<SkillProjection> ReadProjectionAsync(ValueTask<Sunder.Sdk.Services.SunderContent> content)
        => SkillProjectionContent.ReadAsync(content, CancellationToken.None);

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
