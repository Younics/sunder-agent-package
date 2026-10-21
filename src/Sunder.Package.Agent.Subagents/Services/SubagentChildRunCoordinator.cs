using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IChildRunExecutorService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Child_Run_Executor.IChildRunExecutorService;
using IRuntimeCatalogService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Subagents.Services;

internal sealed partial class SubagentChildRunCoordinator(
    IServiceProviders<IRuntimeCatalogService> catalogs,
    IServiceProviders<IChildRunExecutorService> executors,
    SubagentDescriptorSchema descriptors,
    SubagentPermissionStatusAdapter permissionStatusAdapter,
    SubagentBatchResultRenderer resultRenderer)
{
    private readonly SubagentDescriptorSchema _descriptors = descriptors;
    private readonly SubagentPermissionStatusAdapter _permissionStatusAdapter = permissionStatusAdapter;
    private readonly SubagentBatchResultRenderer _resultRenderer = resultRenderer;

    public async ValueTask<(SubagentChildRunEnvironment? Environment, SubagentTaskResult? Failure)> PrepareAsync(
        AgentToolExecutionContext context,
        string resultToolId,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (context.SessionId is null || context.RunId is null || context.RunRevision is null || string.IsNullOrWhiteSpace(context.ToolCallId))
        {
            return (null, _permissionStatusAdapter.CreateTaskFailure(resultToolId, "Task tool requires an active parent run context.", "task-context-missing"));
        }

        AgentProfileRecord? parentProfile = null;
        foreach (var provider in (await catalogs.ListAsync(token).ConfigureAwait(false)).Providers.Where(item => item.State == ServiceProviderState.Active))
        {
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(token, provider.RetirementToken);
            await provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            parentProfile = string.IsNullOrWhiteSpace(context.ProfileId) ? null
                : await provider.Service.GetProfileAsync(context.ProfileId, invocation.Token).ConfigureAwait(false);
            await provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            break;
        }
        if (parentProfile is null)
        {
            return (null, _permissionStatusAdapter.CreateTaskFailure(resultToolId, "Task tool requires the base Agent runtime catalog extension.", "task-runtime-unavailable"));
        }

        var childRunExecutorReference = (await executors.ListAsync(token).ConfigureAwait(false)).Providers
            .FirstOrDefault(item => item.State == ServiceProviderState.Active && !item.RetirementToken.IsCancellationRequested);
        if (childRunExecutorReference is null)
        {
            return (null, _permissionStatusAdapter.CreateTaskFailure(resultToolId, "Task tool requires the base Agent child-run executor extension.", "task-child-executor-unavailable"));
        }

        if (string.IsNullOrWhiteSpace(context.Workspace?.WorkspaceId))
        {
            return (null, _permissionStatusAdapter.CreateTaskFailure(resultToolId, "Task tool requires an active workspace.", "task-workspace-unavailable"));
        }

        if (!await _descriptors.SupportsSubagentFeatureAsync(parentProfile, token).ConfigureAwait(false))
        {
            return (null, _permissionStatusAdapter.CreateTaskFailure(resultToolId, "The task tool is only available to profiles using a behavior loop with subagent support.", "task-loop-disabled"));
        }

        await childRunExecutorReference.EnsureCurrentAsync(token).ConfigureAwait(false);
        return (new SubagentChildRunEnvironment(context, parentProfile, childRunExecutorReference), null);
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
        var executor = environment.ChildRunExecutorReference;
        if (executor.RetirementToken.IsCancellationRequested)
        {
            return _permissionStatusAdapter.CreateTaskFailure(
                resultToolId,
                "The child-run executor package became unavailable before the task started.",
                AgentToolResultErrorCodes.PackageUnavailable);
        }

        AgentChildRunResult result;
            var retirementToken = executor.RetirementToken;
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                retirementToken);
            try
            {
                await executor.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
                result = await executor.Service.RunChildAsync(new AgentChildRunRequest
                {
                    ParentSessionId = context.SessionId!.Value, ParentRunId = context.RunId!.Value, ParentRunRevision = context.RunRevision!.Value,
                    ParentToolCallId = context.ToolCallId!, WorkspaceId = context.Workspace!.WorkspaceId, TaskId = request.TaskId,
                    ChildProfile = childProfile, UserMessage = request.Prompt!, Title = childSessionTitle, AgentKind = "subagent",
                }, invocation.Token);
                await executor.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
                if (retirementToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return _permissionStatusAdapter.CreateTaskFailure(
                        resultToolId,
                        $"Child-run executor package '{executor.PackageId}' became unavailable while the task was running.",
                        AgentToolResultErrorCodes.PackageUnavailable);
                }
            }
            catch (Exception exception) when (SubagentCatalogSelection.IsUnavailable(exception, retirementToken, cancellationToken))
            {
                return _permissionStatusAdapter.CreateTaskFailure(
                    resultToolId,
                    $"Child-run executor package '{executor.PackageId}' became unavailable while the task was running.",
                    AgentToolResultErrorCodes.PackageUnavailable);
            }
        var state = SubagentPermissionStatusAdapter.ToTaskState(result.Status);
        var payload = _resultRenderer.BuildChildSessionPayload(result, subagent, childSessionTitle, state);
        return _permissionStatusAdapter.AdaptChildResult(resultToolId, result, subagent, payload);
    }

    internal static AgentProfileRecord BuildChildProfile(AgentProfileRecord parentProfile, SubagentRecord subagent)
    {
        var now = new SunderRpcOffsetDateTime(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
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
            bindings.Add(new AgentProfileModelBindingRecord { ProfileId = profileId, CapabilityKind = AgentModelCapabilityKinds.Chat,
                ProviderId = chatProviderId, ModelId = chatModelId, SettingsJson = chatSettingsJson, UpdatedAtUtc = now });
        }

        if (embeddingBinding is not null)
        {
            bindings.Add(embeddingBinding with { ProfileId = profileId, UpdatedAtUtc = now });
        }

        return new AgentProfileRecord
        {
            ProfileId = profileId, DisplayName = subagent.DisplayName, Description = subagent.Description, Instructions = subagent.Instructions,
            ChatProviderId = chatProviderId, ChatModelId = chatModelId,
            EmbeddingProviderId = embeddingBinding?.ProviderId ?? parentProfile.EmbeddingProviderId,
            EmbeddingModelId = embeddingBinding?.ModelId ?? parentProfile.EmbeddingModelId,
            CreatedAtUtc = now, UpdatedAtUtc = now, ModelBindings = bindings,
            SelectableCapabilityAssignments = subagent.SelectableCapabilityAssignments ?? [],
            BehaviorLoopId = AgentBehaviorLoopIds.Default, BehaviorLoopSourceId = null, BehaviorLoopSettingsJson = null, IsInternal = true,
        };
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

internal sealed partial record SubagentChildRunEnvironment(
    AgentToolExecutionContext Context,
    AgentProfileRecord ParentProfile,
    ServiceProvider<IChildRunExecutorService> ChildRunExecutorReference);
