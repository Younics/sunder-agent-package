using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyDockerExecution(AgentExecutionTargetContext context, string workspace, DockerArtifactBackend backend,
        AgentServices.IExecutionTargetService client)
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal(8, (await Call("describe", () => client.DescribeAsync())).Facets.Count);
        var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() =>
            Call("resolve-resources", () => client.ResolveResourcesAsync(context, [])));
        Assert.Equal(SunderRpcErrorKind.Domain, unavailable.Error.Kind);
        Assert.Equal("agent.execution.facet-unavailable", unavailable.Error.Code);
        var ready = await Call("readiness", () => client.ReadinessAsync(context));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(AgentExecutionTargetReadinessStatus.Failed, ready.Status);
            return;
        }
        Assert.Equal(AgentExecutionTargetReadinessStatus.Ready, ready.Status);
        Assert.True(backend.Challenges >= 2);
        var generation = await Call("configuration-generation", () => client.ConfigurationGenerationAsync(context));
        Assert.False(string.IsNullOrWhiteSpace(generation));
        context = context with { ExpectedConfigurationGeneration = generation };
        Assert.Equal("/bin/sh", (await Call("shell", () => client.ShellAsync(context))).ExecutablePath);
        var containerRoot = (await Call("execution-scope", () => client.ExecutionScopeAsync(context))).DefaultWorkingDirectory!;
        var path = containerRoot.TrimEnd('/') + "/artifact.txt";
        var hostPath = Path.Combine(workspace, "artifact.txt");
        await File.WriteAllTextAsync(hostPath, "first\nneedle\nthird");
        var read = await Call("read-file", () => client.ReadFileAsync(context, new AgentFileReadRequest(path, 2, 1)));
        Assert.False(read.IsError, read.Content); Assert.Equal("needle", read.Content); Assert.Equal(3, read.TotalLines);
        Assert.Single((await Call("structured-file-search", () => client.StructuredFileSearchAsync(context,
            new AgentFileSearchRequest(containerRoot, AgentFileSearchKind.Grep, "needle")))).Matches);
        Assert.Equal(hostPath, (await Call("map-path", () => client.MapPathAsync(context, path))).HostPath);
        await File.WriteAllTextAsync(Path.Combine(workspace, "AGENTS.md"), "Docker artifact instructions");
        var instructions = await Call("discover-instructions", () => client.DiscoverInstructionsAsync(context,
            new AgentScopedInstructionDiscoveryRequest([new(containerRoot, true)])));
        Assert.Contains(instructions.Scopes.SelectMany(value => value.Documents), value => value.Content == "Docker artifact instructions");
        Assert.True((await Call("read-file", () => client.ReadFileAsync(context,
            new AgentFileReadRequest("/container-private/secret.txt")))).IsError);
        var operation = new AgentResourceOperationContext(Guid.NewGuid(), 3, "call.docker", "files.mutate", 0, "workspace-generation", "binding-generation",
            "sunder.package.agent.tools.files", "sunder.package.agent.execution.docker", Guid.NewGuid().ToString("N"));
        var planning = context with { ResourceOperation = operation };
        var resource = await Call("resolve-file", () => client.ResolveFileAsync(planning, path));
        Assert.NotNull(resource.ResourceClaim); Assert.Empty(resource.AuthorityReferences);
        var approved = planning with { ApprovedResourceReferences = [resource.CanonicalReference], ApprovedResourceClaims = [resource.ResourceClaim!], CapturePostMutationResource = true };
        Assert.True((await Call("validate-resource-authority", () => client.ValidateResourceAuthorityAsync(approved))).IsValid);
        var written = await Call("write-file", () => client.WriteFileAsync(approved, new AgentFileWriteRequest(path, "written")));
        Assert.False(written.IsError, written.Summary); Assert.Equal("written", await File.ReadAllTextAsync(hostPath)); Assert.NotNull(written.PostMutationResource);
        Assert.NotNull(written.PostMutationResource.ResourceClaim);
        await CallVoid("release-resource-authority", () => client.ReleaseResourceAuthorityAsync(resource.AuthorityReferences));
        var deleted = await Call("delete-file", () => client.DeleteFileAsync(context, new AgentFileDeleteRequest(path)));
        Assert.False(deleted.IsError, deleted.Summary); Assert.False(File.Exists(hostPath));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call("write-file",
                () => client.WriteFileAsync(context, new AgentFileWriteRequest(path, "cancelled"), cancelled.Token)));
            Assert.False(File.Exists(hostPath));
        }
        Assert.Equal("controlled-output", (await Call("execute-shell",
            () => client.ExecuteShellAsync(context, new AgentShellCommandRequest("artifact-shell")))).Output);
        Assert.Equal("controlled-output", (await Call("execute-process",
            () => client.ExecuteProcessAsync(context, new AgentProcessCommandRequest("artifact-process", ["one", "two words"])))).Output);
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = Call("execute-shell", () => client.ExecuteShellAsync(context, new AgentShellCommandRequest("artifact-block"), cancelled.Token));
            await backend.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        backend.ImageIdentity = "sha256:" + new string('b', 64);
        await Assert.ThrowsAsync<SunderRpcException>(() => Call("read-file",
            () => client.ReadFileAsync(context, new AgentFileReadRequest(containerRoot + "/AGENTS.md"))));
        backend.ImageIdentity = "sha256:" + new string('a', 64);
        context = context with { ExpectedConfigurationGeneration = null };
        generation = await Call("configuration-generation", () => client.ConfigurationGenerationAsync(context));
        Assert.False(string.IsNullOrWhiteSpace(generation));
        await CallVoid("add-path", () => client.AddPathAsync(context, "/opt/artifact-tools"));
        Assert.Contains("/opt/artifact-tools", await Call("list-path", () => client.ListPathAsync(context)));
        await Assert.ThrowsAsync<SunderRpcException>(() => Call("read-file",
            () => client.ReadFileAsync(context with { ExpectedConfigurationGeneration = generation }, new AgentFileReadRequest(containerRoot + "/AGENTS.md"))));
        Assert.Equal(CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ExecutionTargets).Services.Single().Methods
            .Select(value => value.MethodId).Order(StringComparer.Ordinal), covered.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(workspace).Select(Path.GetFileName),
            value => value!.StartsWith(".sunder-secure-challenge-", StringComparison.Ordinal));

        async Task<T> Call<T>(string method, Func<ValueTask<T>> invoke)
        {
            covered.Add(method);
            return await invoke();
        }

        async Task CallVoid(string method, Func<ValueTask> invoke)
        {
            covered.Add(method);
            await invoke();
        }
    }
}
