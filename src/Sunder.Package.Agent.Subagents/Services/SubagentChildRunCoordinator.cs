using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Subagents.Services;

internal sealed class SubagentChildRunCoordinator(
    IPackageExtensionCatalog extensionCatalog,
    SubagentDescriptorSchema descriptors,
    SubagentPermissionStatusAdapter permissionStatusAdapter,
    SubagentBatchResultRenderer resultRenderer)
{
    private readonly IPackageExtensionCatalog _extensionCatalog = extensionCatalog;
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

        var runtimeCatalog = _extensionCatalog.GetExtensions(PackageExtensionPoints.RuntimeCatalogs).FirstOrDefault();
        if (runtimeCatalog is null)
        {
            failure = _permissionStatusAdapter.CreateTaskFailure(resultToolId, "Task tool requires the base Agent runtime catalog extension.", "task-runtime-unavailable");
            return false;
        }

        var childRunExecutor = _extensionCatalog.GetExtensions(PackageExtensionPoints.ChildRunExecutors).FirstOrDefault();
        if (childRunExecutor is null)
        {
            failure = _permissionStatusAdapter.CreateTaskFailure(resultToolId, "Task tool requires the base Agent child-run executor extension.", "task-child-executor-unavailable");
            return false;
        }

        if (string.IsNullOrWhiteSpace(context.Workspace?.WorkspaceId))
        {
            failure = _permissionStatusAdapter.CreateTaskFailure(resultToolId, "Task tool requires an active workspace.", "task-workspace-unavailable");
            return false;
        }

        var parentProfile = string.IsNullOrWhiteSpace(context.ProfileId) ? null : runtimeCatalog.GetProfile(context.ProfileId);
        if (!_descriptors.SupportsSubagentFeature(parentProfile))
        {
            failure = _permissionStatusAdapter.CreateTaskFailure(resultToolId, "The task tool is only available to profiles using a behavior loop with subagent support.", "task-loop-disabled");
            return false;
        }

        environment = new SubagentChildRunEnvironment(context, parentProfile!, childRunExecutor);
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
        var result = await environment.ChildRunExecutor.RunChildAsync(
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
            cancellationToken);
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
    IAgentChildRunExecutor ChildRunExecutor);
