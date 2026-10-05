using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeAgentAppComposition(string path, string text)
    {
        var normalized = path.Replace('\\', '/');
        var name = Path.GetFileName(path);
        const string shared = "[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]\n";
        if (normalized.Contains("/src/Sunder.Package.Agent.Contracts/", StringComparison.Ordinal)
            && name is "IAgentExecutionTarget.cs" or "IAgentPermissionAwareToolSource.cs" or "IAgentToolExecutionPreflightSource.cs" or "IAgentToolSource.cs")
        {
            Replace(shared, "");
            return text;
        }
        if (!normalized.Contains("/src/Sunder.Package.Agent/", StringComparison.Ordinal)) return text;
        var sharedCount = name switch
        {
            "AgentRuntimeContracts.cs" => 47,
            "AgentPresentationGateways.cs" => 9,
            "HistorySearchModels.cs" => 18,
            "AgentRpcInvocation.cs" => 4,
            "AgentPendingPermissionRequestRecord.cs" or "AgentExecutionTargetWarmupResult.cs" or "AgentTranscriptAnchorContracts.cs"
                or "AgentPermissionActionDescriptor.cs" or "AgentResolvedResource.cs" => 2,
            "HistorySearchText.cs" or "HistorySecretRedactor.cs" or "AgentRuntimePayloadLimits.cs" or "AgentOwnerOperationException.cs"
                or "AgentTranscriptPageCursor.cs" or "AgentSessionPermissionState.cs" or "AgentBehaviorLoopDescriptor.cs"
                or "AgentSessionTitleDefaults.cs" or "AgentToolCatalogEntry.cs" or "AgentWorkspaceBindingRoles.cs"
                or "AgentConsumerValues.cs" or "AgentRpcContractIds.cs" or "AgentToolBindings.cs" or "AgentToolValues.cs"
                or "GeneratedProviderCallLifetime.cs" or "GeneratedToolSourceClient.cs"
                or "AgentAttachmentInfo.cs" or "AgentAttachmentKind.cs" or "AgentAttachmentMetadata.cs" or "AgentAttachmentUploadRequest.cs"
                or "AgentRunActivityUpdate.cs" or "AgentExecutionTargetReadiness.cs" or "AgentExecutionTargetReadinessStatus.cs"
                or "AgentAuthMode.cs" or "AgentChatModelSettings.cs" or "AgentChatModelSettingsJson.cs" or "AgentEmbeddingProviderDescriptor.cs"
                or "AgentEmbeddingProviderReadiness.cs" or "AgentModelCapabilityKinds.cs" or "AgentProviderDescriptor.cs"
                or "AgentFileDeleteRequest.cs" or "AgentFileMutationResult.cs" or "AgentFileReadRequest.cs" or "AgentFileReadResult.cs" or "AgentFileWriteRequest.cs"
                or "AgentPermissionDecision.cs" or "AgentPermissionOverride.cs" or "AgentPermissionRequest.cs" or "AgentShellCommandResult.cs"
                or "AgentToolExecutionContext.cs" or "AgentToolReadiness.cs" or "AgentToolReadinessStatus.cs" or "AgentToolRequest.cs"
                or "AgentToolResult.cs" or "AgentToolResultErrorCodes.cs" or "AgentToolSourceContext.cs" => 1,
            _ => 0,
        };
        if (sharedCount != 0)
        {
            Assert.Equal(sharedCount + 1, text.Split(shared, StringSplitOptions.None).Length);
            text = text.Replace(shared, "", StringComparison.Ordinal);
        }
        var view = name switch
        {
            "AgentChatView.axaml.cs" => ("chat", "Agent Chat", "chat", "Middle"),
            "AgentWorkspacesView.axaml.cs" => ("workspaces", "Workspaces", "workspace", "RightTop"),
            "AgentProfilesView.axaml.cs" => ("profiles", "Agents", "profile", "RightTop"),
            "AgentHistorySearchView.axaml.cs" => ("history", "History", "session", "RightTop"),
            _ => default,
        };
        if (view.Item1 is not null)
            Replace($"[Sunder.Sdk.Packaging.SunderPackageView(\"sunder.package.agent.{view.Item1}\", \"{view.Item2}\",\n"
                + $"    IconPath = \"Assets/{view.Item3}-icon.png\", DefaultPlacement = PackageViewPlacement.{view.Item4})]\n", "");
        if (name == "AgentPermissionsView.axaml.cs") Replace("[Sunder.Sdk.Packaging.SunderPackageSettingsView]\n", "");
        if (name == "AgentToolPresentationService.cs") Replace("public sealed partial class AgentToolPresentationService", "public sealed class AgentToolPresentationService");
        if (name == "AgentHistorySearchView.axaml.cs")
        {
            Replace("    public AgentHistorySearchView(IServiceProvider services)\n        : this(services.GetRequiredService<IAgentHistorySearchGateway>(),\n"
                + "            services.GetRequiredService<AgentChatSelectionStateService>(),\n            services.GetRequiredService<IPackageShellViewService>()) { }\n\n", "");
            Replace("    internal AgentHistorySearchView(\n        IAgentHistorySearchGateway gateway,\n        AgentChatSelectionStateService selectionState,\n        IPackageShellViewService shellViewService)",
                "    public AgentHistorySearchView(IServiceProvider services)");
            Replace("            gateway,\n            selectionState,\n            shellViewService);",
                "            services.GetRequiredService<IAgentHistorySearchGateway>(),\n            services.GetRequiredService<AgentChatSelectionStateService>(),\n            services.GetRequiredService<IPackageShellViewService>());");
        }
        return text;

        void Replace(string current, string previous)
        {
            Assert.Equal(2, text.Split(current, StringSplitOptions.None).Length);
            text = text.Replace(current, previous, StringComparison.Ordinal);
        }
    }
}
