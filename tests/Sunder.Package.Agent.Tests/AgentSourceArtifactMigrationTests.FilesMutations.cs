using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyFilesMutations(FilesTargetProbe target, string workspace, string outside, AgentToolExecutionContext context,
        Func<string, string, object, AgentToolExecutionContext?, CancellationToken, Task<AgentToolResult>> tool,
        Func<string, string, object, CancellationToken, Task<JsonElement>> rpc, AgentRpcProviderHandle handle, string? generation)
    {
        var added = await tool("execute", "apply_patch", new { patchText = "*** Begin Patch\n*** Add File: patch.txt\n+patched\n*** End Patch" }, null, default);
        Assert.False(added.IsError, added.Content);
        Assert.Equal("patched", (await File.ReadAllTextAsync(Path.Combine(workspace, "patch.txt"))).TrimEnd());
        var deleted = await tool("execute", "apply_patch", new { patchText = "*** Begin Patch\n*** Delete File: patch.txt\n*** End Patch" }, null, default);
        Assert.False(deleted.IsError, deleted.Content); Assert.False(File.Exists(Path.Combine(workspace, "patch.txt")));
        await File.WriteAllTextAsync(Path.Combine(workspace, "a.txt"), "a\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "b.txt"), "b\n");
        var raced = false;
        target.BeforeWrite = request =>
        {
            if (!raced && Path.GetFileName(request.GetProperty("request").GetProperty("path").GetString()) == "b.txt")
            { raced = true; File.WriteAllText(Path.Combine(workspace, "b.txt"), "raced\n"); }
        };
        AgentToolResult failed;
        try
        {
            failed = await tool("execute", "apply_patch", new { patchText = "*** Begin Patch\n*** Update File: a.txt\n@@\n-a\n+A\n*** Update File: b.txt\n@@\n-b\n+B\n*** End Patch" }, null, default);
        }
        finally { target.BeforeWrite = null; }
        Assert.True(raced); Assert.True(failed.IsError);
        Assert.Equal("a\n", await File.ReadAllTextAsync(Path.Combine(workspace, "a.txt")));
        Assert.Equal("raced\n", await File.ReadAllTextAsync(Path.Combine(workspace, "b.txt")));
        Assert.Contains("No applied patch changes remain", failed.Content!, StringComparison.Ordinal);
        Assert.Contains(target.Calls, call => call.Method == "write-file" && call.Request.GetProperty("context").GetProperty("capturePostMutationResource").GetBoolean()
            && call.Response.GetProperty("postMutationResource").ValueKind == JsonValueKind.Object);

        var outsideFile = Path.Combine(outside, "approved.txt"); await File.WriteAllTextAsync(outsideFile, "outside");
        // Resource-authority transport is also available outside a durable transcript.
        // The separate durable-session scenarios verify instruction acknowledgment.
        var planning = context with { SessionId = null, TranscriptEpoch = 0, AllowOutsideConfiguredScope = true };
        var permission = ReadToolPermission(await rpc("files.tools", "permission", RegressionTestRpcValues.ToolInvocation(
            planning with { ApprovedResourceCapabilities = [], ExecutionTargetConfigurationGeneration = generation },
            new("write", JsonSerializer.Serialize(new { path = outsideFile, content = "approved" })), handle), default));
        Assert.Equal(AgentPermissionBoundaryIds.OutsideConfiguredScope, permission.BoundaryId);
        Assert.NotEmpty(permission.ResourceCapabilities); Assert.NotEmpty(permission.ResourceClaims);
        var approved = planning with
        {
            ApprovedResourceCapabilities = permission.ResourceCapabilities,
            ApprovedResourceReferences = permission.ResourceReferences,
            ApprovedResourceClaims = permission.ResourceClaims,
            ResourceOperation = planning.ResourceOperation! with { CanIssueOutsideAuthority = false },
        };
        var wrongAuthority = approved with { ResourceOperation = approved.ResourceOperation! with { AuthorityActivationId = Guid.NewGuid().ToString("N") } };
        Assert.True((await tool("preflight", "write", new { path = outsideFile, content = "denied" }, wrongAuthority, default)).IsError);
        Assert.Equal("outside", await File.ReadAllTextAsync(outsideFile));
        var written = await tool("execute", "write", new { path = outsideFile, content = "approved" }, approved, default);
        Assert.False(written.IsError, written.Content); Assert.Equal("approved", await File.ReadAllTextAsync(outsideFile));
        Assert.Contains(target.Calls, call => call.Method == "release-resource-authority" && call.Request.GetProperty("capabilities").GetArrayLength() > 0);
        Assert.Contains(target.Calls, call => call.Method == "write-file"
            && call.Request.GetProperty("context").GetProperty("approvedResourceCapabilities").GetArrayLength() > 0
            && call.Request.GetProperty("context").GetProperty("resourceOperation").GetProperty("authorityActivationId").GetString() == approved.ResourceOperation!.AuthorityActivationId);
        var denied = await tool("execute", "write", new { path = outsideFile, content = "reused" }, approved, default);
        Assert.True(denied.IsError); Assert.Equal("approved", await File.ReadAllTextAsync(outsideFile));
        var stale = await Assert.ThrowsAsync<SunderRpcException>(() => tool("execute", "write", new { path = "a.txt", content = "stale" }, context with { ExecutionTargetConfigurationGeneration = "changed-generation" }, default));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, stale.Error.Kind);
        Assert.Equal("a\n", await File.ReadAllTextAsync(Path.Combine(workspace, "a.txt")));
    }
}
