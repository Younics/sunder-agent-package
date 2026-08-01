using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Subagents.Services;

internal sealed class SubagentChildRunCoordinator(
    AgentRpcCatalog rpcCatalog,
    SubagentDescriptorSchema descriptors,
    SubagentPermissionStatusAdapter permissionStatusAdapter,
    SubagentBatchResultRenderer resultRenderer)
{
    private readonly SubagentDescriptorSchema _descriptors = descriptors;
    private readonly SubagentPermissionStatusAdapter _permissionStatusAdapter = permissionStatusAdapter;
    private readonly SubagentBatchResultRenderer _resultRenderer = resultRenderer;

    public bool TryPrepare(
        AgentToolExecutionContext context,
        string resultToolId,
        out SubagentChildRunEnvironment? environment,
        out SubagentTaskResult? failure)
    {
        environment = null;
        failure = null;
        if (context.SessionId is null || context.RunId is null || context.RunRevision is null || string.IsNullOrWhiteSpace(context.ToolCallId))
        {
            failure = _permissionStatusAdapter.CreateTaskFailure(resultToolId, "Task tool requires an active parent run context.", "task-context-missing");
            return false;
        }

        AgentProfileRecord? parentProfile = null;
        foreach (var reference in rpcCatalog.GetServiceReferences(AgentRpcServices.RuntimeCatalogs))
        {
            if (!reference.TryAcquire(out var lease))
            {
                continue;
            }
            using (lease)
            {
                parentProfile = string.IsNullOrWhiteSpace(context.ProfileId)
                    ? null
                    : lease.Service.GetProfile(context.ProfileId);
                if (!lease.RetirementToken.IsCancellationRequested)
                {
                    break;
                }
                parentProfile = null;
            }
        }
        if (parentProfile is null)
        {
            failure = _permissionStatusAdapter.CreateTaskFailure(resultToolId, "Task tool requires the base Agent runtime catalog extension.", "task-runtime-unavailable");
            return false;
        }

        var childRunExecutorReference = rpcCatalog
            .GetServiceReferences(AgentRpcServices.ChildRunExecutors)
            .FirstOrDefault(IsAvailable);
        if (childRunExecutorReference is null)
        {
            failure = _permissionStatusAdapter.CreateTaskFailure(resultToolId, "Task tool requires the base Agent child-run executor extension.", "task-child-executor-unavailable");
            return false;
        }

        if (string.IsNullOrWhiteSpace(context.Workspace?.WorkspaceId))
        {
            failure = _permissionStatusAdapter.CreateTaskFailure(resultToolId, "Task tool requires an active workspace.", "task-workspace-unavailable");
            return false;
        }

        if (!_descriptors.SupportsSubagentFeature(parentProfile))
        {
            failure = _permissionStatusAdapter.CreateTaskFailure(resultToolId, "The task tool is only available to profiles using a behavior loop with subagent support.", "task-loop-disabled");
            return false;
        }

        environment = new SubagentChildRunEnvironment(context, parentProfile, childRunExecutorReference);
        return true;
    }

    public async ValueTask<SubagentTaskResult> RunAsync(
        SubagentChildRunEnvironment environment,
        string resultToolId,
        SubagentTaskRequest request,
        SubagentRecord subagent,
        CancellationToken cancellationToken)
    {
        var childProfile = BuildChildProfile(environment.ParentProfile, subagent);
        var childSessionTitle = string.IsNullOrWhiteSpace(request.Description)
            ? subagent.DisplayName
            : request.Description.Trim();
        var context = environment.Context;
        if (!environment.ChildRunExecutorReference.TryAcquire(out var executorLease))
        {
            return _permissionStatusAdapter.CreateTaskFailure(
                resultToolId,
                "The child-run executor package became unavailable before the task started.",
                AgentToolResultErrorCodes.PackageUnavailable);
        }

        AgentChildRunResult result;
        using (executorLease)
        {
            var retirementToken = executorLease.RetirementToken;
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                retirementToken);
            try
            {
                result = await executorLease.Service.RunChildAsync(
                    new AgentChildRunRequest(
                        context.SessionId!.Value,
                        context.RunId!.Value,
                        context.RunRevision!.Value,
                        context.ToolCallId!,
                        context.Workspace!.WorkspaceId,
                        request.TaskId,
                        childProfile,
                        request.Prompt!,
                        childSessionTitle),
                    invocation.Token);
                if (retirementToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return _permissionStatusAdapter.CreateTaskFailure(
                        resultToolId,
                        $"Child-run executor package '{executorLease.PackageId}' became unavailable while the task was running.",
                        AgentToolResultErrorCodes.PackageUnavailable);
                }
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested
                && (retirementToken.IsCancellationRequested
                    || exception is SunderRpcException
                    {
                        Error.Kind: SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable,
                    }))
            {
                return _permissionStatusAdapter.CreateTaskFailure(
                    resultToolId,
                    $"Child-run executor package '{executorLease.PackageId}' became unavailable while the task was running.",
                    AgentToolResultErrorCodes.PackageUnavailable);
            }
        }
        var state = SubagentPermissionStatusAdapter.ToTaskState(result.Status);
        var payload = _resultRenderer.BuildChildSessionPayload(result, subagent, childSessionTitle, state);
        return _permissionStatusAdapter.AdaptChildResult(resultToolId, result, subagent, payload);
    }

    internal static AgentProfileRecord BuildChildProfile(AgentProfileRecord parentProfile, SubagentRecord subagent)
    {
        var now = DateTimeOffset.UtcNow;
        var profileId = $"subagent-{subagent.SubagentId}-{BuildProfileSnapshotId(parentProfile, subagent)}";
        var parentChatBinding = FindModelBinding(parentProfile, AgentModelCapabilityKinds.Chat);
        var hasChatOverride = !string.IsNullOrWhiteSpace(subagent.ChatProviderId);
        var chatProviderId = hasChatOverride ? subagent.ChatProviderId : parentChatBinding?.ProviderId ?? parentProfile.ChatProviderId;
        var chatModelId = hasChatOverride ? subagent.ChatModelId : parentChatBinding?.ModelId ?? parentProfile.ChatModelId;
        var chatSettingsJson = hasChatOverride ? subagent.ChatModelSettingsJson : parentChatBinding?.SettingsJson;
        var embeddingBinding = FindModelBinding(parentProfile, AgentModelCapabilityKinds.Embedding);
        var bindings = new List<AgentProfileModelBindingRecord>();
        if (!string.IsNullOrWhiteSpace(chatProviderId) || !string.IsNullOrWhiteSpace(chatModelId))
        {
            bindings.Add(new AgentProfileModelBindingRecord(profileId, AgentModelCapabilityKinds.Chat, chatProviderId, chatModelId, chatSettingsJson, now));
        }

        if (embeddingBinding is not null)
        {
            bindings.Add(embeddingBinding with { ProfileId = profileId, UpdatedAtUtc = now });
        }

        return new AgentProfileRecord(
            profileId,
            subagent.DisplayName,
            subagent.Description,
            subagent.Instructions,
            chatProviderId,
            chatModelId,
            embeddingBinding?.ProviderId ?? parentProfile.EmbeddingProviderId,
            embeddingBinding?.ModelId ?? parentProfile.EmbeddingModelId,
            now,
            now,
            bindings,
            subagent.SelectableCapabilityAssignments ?? [],
            AgentBehaviorLoopIds.Default,
            null,
            null,
            IsInternal: true);
    }

    private static AgentProfileModelBindingRecord? FindModelBinding(AgentProfileRecord profile, string capabilityKind)
        => profile.ModelBindings?.FirstOrDefault(binding =>
            string.Equals(binding.CapabilityKind, capabilityKind, StringComparison.OrdinalIgnoreCase));

    private static bool IsAvailable(AgentRpcReference<IAgentChildRunExecutor> reference)
    {
        if (!reference.TryAcquire(out var lease))
        {
            return false;
        }
        using (lease)
        {
            return !lease.RetirementToken.IsCancellationRequested;
        }
    }

    private static string BuildProfileSnapshotId(
        AgentProfileRecord parentProfile,
        SubagentRecord subagent)
    {
        var snapshot = JsonSerializer.Serialize(new
        {
            ParentProfileId = parentProfile.ProfileId,
            ParentUpdatedAtUtc = parentProfile.UpdatedAtUtc,
            ParentModelBindings = parentProfile.ModelBindings,
            ParentChatProviderId = parentProfile.ChatProviderId,
            ParentChatModelId = parentProfile.ChatModelId,
            ParentEmbeddingProviderId = parentProfile.EmbeddingProviderId,
            ParentEmbeddingModelId = parentProfile.EmbeddingModelId,
            SubagentId = subagent.SubagentId,
            SubagentUpdatedAtUtc = subagent.UpdatedAtUtc,
            SubagentChatProviderId = subagent.ChatProviderId,
            SubagentChatModelId = subagent.ChatModelId,
            subagent.ChatModelSettingsJson,
            subagent.Instructions,
            subagent.SelectableCapabilityAssignments,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)))
            .ToLowerInvariant()[..16];
    }
}

internal sealed record SubagentChildRunEnvironment(
    AgentToolExecutionContext Context,
    AgentProfileRecord ParentProfile,
    AgentRpcReference<IAgentChildRunExecutor> ChildRunExecutorReference);
