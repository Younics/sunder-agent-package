using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyDockerExecution(AgentExecutionTargetContext context, string workspace, DockerArtifactBackend backend,
        Func<string, object, CancellationToken, Task<JsonElement>> invoke)
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var descriptor = await Call("describe", new { });
        Assert.Equal(8, descriptor.GetProperty("facets").GetArrayLength());
        await Assert.ThrowsAsync<NotSupportedException>(() => Call("resolve-resources", new
        { context = ExecutionWireContext(context), resources = Array.Empty<object>() }));
        var ready = await Call("readiness", ExecutionWireContext(context));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("failed", ready.GetProperty("status").GetString());
            return;
        }
        Assert.Equal("ready", ready.GetProperty("status").GetString());
        Assert.True(backend.Challenges >= 2);
        var generation = (await Call("configuration-generation", ExecutionWireContext(context))).GetString()!;
        context = context with { ExpectedConfigurationGeneration = generation };
        Assert.Equal("/bin/sh", (await Call("shell", ExecutionWireContext(context))).GetProperty("executablePath").GetString());
        var scope = await Call("execution-scope", ExecutionWireContext(context));
        var containerRoot = scope.GetProperty("defaultWorkingDirectory").GetString()!;
        var path = containerRoot.TrimEnd('/') + "/artifact.txt";
        var hostPath = Path.Combine(workspace, "artifact.txt");
        await File.WriteAllTextAsync(hostPath, "first\nneedle\nthird");
        var read = await Call("read-file", new { context = ExecutionWireContext(context), request = new AgentFileReadRequest(path, 2, 1) });
        Assert.False(read.GetProperty("isError").GetBoolean(), read.GetRawText());
        Assert.Equal("needle", read.GetProperty("content").GetString()); Assert.Equal(3, read.GetProperty("totalLines").GetInt32());
        var search = await Call("structured-file-search", new { context = ExecutionWireContext(context), request = new AgentFileSearchRequest(containerRoot, AgentFileSearchKind.Grep, "needle") });
        Assert.Single(search.GetProperty("matches").EnumerateArray());
        Assert.Equal(hostPath, (await Call("map-path", new { context = ExecutionWireContext(context), path })).GetProperty("hostPath").GetString());
        await File.WriteAllTextAsync(Path.Combine(workspace, "AGENTS.md"), "Docker artifact instructions");
        var instructions = await Call("discover-instructions", new { context = ExecutionWireContext(context), request = new AgentScopedInstructionDiscoveryRequest([new(containerRoot, true)]) });
        Assert.Contains(instructions.GetProperty("scopes").EnumerateArray().SelectMany(value => value.GetProperty("documents").EnumerateArray()), value => value.GetProperty("content").GetString() == "Docker artifact instructions");
        var outside = await Call("read-file", new { context = ExecutionWireContext(context), request = new AgentFileReadRequest("/container-private/secret.txt") });
        Assert.True(outside.GetProperty("isError").GetBoolean());
        var operation = new AgentResourceOperationContext(Guid.NewGuid(), 3, "call.docker", "files.mutate", 0, "workspace-generation", "binding-generation",
            "sunder.package.agent.tools.files", "sunder.package.agent.execution.docker", Guid.NewGuid().ToString("N"));
        var planning = context with { ResourceOperation = operation };
        var resource = ReadExecutionResource(await Call("resolve-file", new { context = ExecutionWireContext(planning), path }));
        Assert.NotNull(resource.ResourceClaim); Assert.Empty(resource.AuthorityReferences);
        var approved = planning with { ApprovedResourceReferences = [resource.CanonicalReference], ApprovedResourceClaims = [resource.ResourceClaim!], CapturePostMutationResource = true };
        Assert.True((await Call("validate-resource-authority", ExecutionWireContext(approved))).GetProperty("isValid").GetBoolean());
        var written = ReadExecutionMutation(await Call("write-file", new { context = ExecutionWireContext(approved), request = new AgentFileWriteRequest(path, "written") }));
        Assert.False(written.IsError, written.Summary); Assert.Equal("written", await File.ReadAllTextAsync(hostPath)); Assert.NotNull(written.PostMutationResource);
        Assert.NotNull(written.PostMutationResource.ResourceClaim);
        await Call("release-resource-authority", new { capabilities = resource.AuthorityReferences });
        var deleted = ReadExecutionMutation(await Call("delete-file", new { context = ExecutionWireContext(context), request = new AgentFileDeleteRequest(path) }));
        Assert.False(deleted.IsError, deleted.Summary); Assert.False(File.Exists(hostPath));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call("write-file", new { context = ExecutionWireContext(context), request = new AgentFileWriteRequest(path, "cancelled") }, cancelled.Token));
            Assert.False(File.Exists(hostPath));
        }
        Assert.Equal("controlled-output", (await Call("execute-shell", new { context = ExecutionWireContext(context), request = new AgentShellCommandRequest("artifact-shell") })).GetProperty("output").GetString());
        Assert.Equal("controlled-output", (await Call("execute-process", new { context = ExecutionWireContext(context), request = new AgentProcessCommandRequest("artifact-process", ["one", "two words"]) })).GetProperty("output").GetString());
        using (var cancelled = new CancellationTokenSource())
        {
            var pending = Call("execute-shell", new { context = ExecutionWireContext(context), request = new AgentShellCommandRequest("artifact-block") }, cancelled.Token);
            await backend.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        backend.ImageIdentity = "sha256:" + new string('b', 64);
        await Assert.ThrowsAsync<SunderRpcException>(() => Call("read-file", new { context = ExecutionWireContext(context), request = new AgentFileReadRequest(containerRoot + "/AGENTS.md") }));
        backend.ImageIdentity = "sha256:" + new string('a', 64);
        context = context with { ExpectedConfigurationGeneration = null };
        generation = (await Call("configuration-generation", ExecutionWireContext(context))).GetString()!;
        await Call("add-path", new { context = ExecutionWireContext(context), path = "/opt/artifact-tools" });
        Assert.Contains((await Call("list-path", ExecutionWireContext(context))).EnumerateArray(), value => value.GetString() == "/opt/artifact-tools");
        await Assert.ThrowsAsync<SunderRpcException>(() => Call("read-file", new { context = ExecutionWireContext(context with { ExpectedConfigurationGeneration = generation }), request = new AgentFileReadRequest(containerRoot + "/AGENTS.md") }));
        Assert.Equal(RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ExecutionTarget).Services.Single().Methods.Select(value => value.MethodId).Order(StringComparer.Ordinal), covered.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(workspace).Select(Path.GetFileName),
            value => value!.StartsWith(".sunder-secure-challenge-", StringComparison.Ordinal));
        Task<JsonElement> Call(string method, object request, CancellationToken token = default) { covered.Add(method); return invoke(method, request, token); }
    }
}
