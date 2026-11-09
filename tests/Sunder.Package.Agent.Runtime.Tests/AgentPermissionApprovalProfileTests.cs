using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentPermissionApprovalProfileTests
{
    [Theory]
    [InlineData("narrower", 1)]
    [InlineData("single-reference", 0)]
    [InlineData("references", 0)]
    [InlineData("capability", 0)]
    [InlineData("outside", 0)]
    [InlineData("denied", 0)]
    public async Task RetainedGenericApprovalAllowsNarrowerDescriptionButNotNewAuthorityOrPolicyDenial(string change, int executions)
    {
        var source = new PermissionAwareMutationToolSource("mutate") { OmitPermissionRequest = true };
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        Assert.Equal(AgentPermissionService.GenericMutationActionId, pending.ActionId);
        source.OmitPermissionRequest = false;
        source.PermissionOverride = new("test.mutate", "test-boundary", "Describe the same prepared action.", IsMutation: true);
        source.PermissionOverride = change switch
        {
            "single-reference" => source.PermissionOverride with { ResourceReference = "new-authority" },
            "references" => source.PermissionOverride with { ResourceReferences = ["new-authority"] },
            "capability" => source.PermissionOverride with { ResourceCapabilities = ["new-authority"] },
            "outside" => source.PermissionOverride with { BoundaryId = AgentPermissionBoundaryIds.OutsideConfiguredScope },
            _ => source.PermissionOverride,
        };
        if (change == "denied") runtime.PermissionService.SaveOverride("test.mutate", "test-boundary", AgentPermissionDecision.Deny);

        await runtime.ResumeCoordinator.ApproveAsync(runtime.Session.SessionId, pending.RequestId);
        await runtime.ResumeCoordinator.ApproveAsync(runtime.Session.SessionId, pending.RequestId);

        Assert.Equal(executions, source.ExecutionCount);
        Assert.Equal(executions == 1, runtime.Store.GetPermissionRequest(runtime.Session.SessionId, pending.RequestId)?.Status == AgentPendingPermissionStatus.Executed);
        Assert.Empty(runtime.Store.ListSessionPermissionApprovals(runtime.Session.SessionId));
    }

    [Fact]
    public async Task MissingNewModelDoesNotPreventTheApprovedActionFromExecuting()
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        var profile = Assert.IsType<AgentProfileRecord>(runtime.Store.GetProfile(pending.ProfileId!));
        runtime.Store.SaveProfile(profile with
        {
            ChatProviderId = "unavailable-provider",
            ModelBindings = profile.ModelBindings!.Select(binding => binding with { ProviderId = "unavailable-provider" }).ToArray(),
        });

        var completed = await runtime.ResumeCoordinator.ApproveAsync(runtime.Session.SessionId, pending.RequestId);

        Assert.Equal(1, source.ExecutionCount);
        Assert.Equal(AgentPendingPermissionStatus.Executed,
            runtime.Store.GetPermissionRequest(runtime.Session.SessionId, pending.RequestId)?.Status);
        Assert.Equal(AgentRunStatus.Interrupted, completed?.Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ApprovalExecutesOriginalCallAfterProfileSave(bool changeAssignments, bool losePreparedState)
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        var profile = Assert.IsType<AgentProfileRecord>(runtime.Store.GetProfile(pending.ProfileId!));
        runtime.Store.SaveProfile(profile with
        {
            DisplayName = "Saved while approval was open",
            Instructions = "Instructions for subsequent provider requests.",
            UpdatedAtUtc = profile.UpdatedAtUtc.AddSeconds(1),
            ModelBindings = profile.ModelBindings!.Select(binding => binding with
            {
                UpdatedAtUtc = binding.UpdatedAtUtc.AddSeconds(1),
            }).ToArray(),
            SelectableCapabilityAssignments = changeAssignments
                ? [new(AgentProfileSelectableCapabilityKinds.Tool, "another-tool", source.SourceId)]
                : profile.SelectableCapabilityAssignments,
        });
        if (losePreparedState) await runtime.ForgetPreparedInvocation(pending);

        var completed = await runtime.ResumeCoordinator.ApproveAsync(runtime.Session.SessionId, pending.RequestId);
        await runtime.ResumeCoordinator.ApproveAsync(runtime.Session.SessionId, pending.RequestId);

        Assert.Equal(AgentRunStatus.Completed, completed?.Status);
        Assert.Equal(1, source.ExecutionCount);
        var execution = Assert.IsType<AgentToolExecutionRecord>(runtime.Store.GetToolExecution(pending.ToolExecutionId!.Value));
        Assert.Equal(AgentToolExecutionStatus.Completed, execution.Status);
        Assert.Equal(AgentToolInvocationFingerprint.Create(pending.ToolId!, pending.ArgumentsJson), execution.InvocationFingerprint);
        Assert.Equal(AgentPendingPermissionStatus.Executed,
            runtime.Store.GetPermissionRequest(runtime.Session.SessionId, pending.RequestId)?.Status);
        Assert.Empty(runtime.Store.ListSessionPermissionApprovals(runtime.Session.SessionId));
    }

    [Fact]
    public async Task ProfileSaveDuringApprovalRevalidationDoesNotRevokeSessionApproval()
    {
        var source = new PermissionAwareMutationToolSource("mutate");
        await using var runtime = await PermissionHardeningRuntime.CreateAsync(source);
        var pending = await runtime.CreatePendingRequestAsync();
        source.BlockPermissionResolution();
        var approval = runtime.ResumeCoordinator.ApproveAsync(runtime.Session.SessionId, pending.RequestId, approveForSession: true);
        try
        {
            await source.PermissionResolutionStarted.WaitAsync(TimeSpan.FromSeconds(10));
            var profile = Assert.IsType<AgentProfileRecord>(runtime.Store.GetProfile(pending.ProfileId!));
            runtime.Store.SaveProfile(profile with { UpdatedAtUtc = profile.UpdatedAtUtc.AddSeconds(1) });
        }
        finally
        {
            source.ReleasePermissionResolution();
        }

        var completed = await approval.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AgentRunStatus.Completed, completed?.Status);
        Assert.Equal(1, source.ExecutionCount);
        var grant = Assert.Single(runtime.Store.ListSessionPermissionApprovals(runtime.Session.SessionId));
        Assert.Equal(pending.ActionId, grant.ActionId);
        Assert.Equal(pending.BoundaryId, grant.Pattern);
    }
}
