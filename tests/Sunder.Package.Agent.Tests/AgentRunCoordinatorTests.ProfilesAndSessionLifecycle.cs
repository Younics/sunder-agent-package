extern alias AgentCore;
extern alias LocalWorker;
extern alias McpWorker;
extern alias MemoryWorker;
extern alias AnthropicWorker;
extern alias GeminiWorker;
extern alias SkillsWorker;
extern alias SkillsApp;
extern alias SubagentsWorker;
extern alias SubagentsApp;
extern alias FilesWorker;
extern alias ShellWorker;

using System.Collections.Specialized;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using LocalWorker::Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.HistorySearch;
using McpWorker::Sunder.Package.Agent.Mcp;
using McpWorker::Sunder.Package.Agent.Mcp.Runtime;
using McpWorker::Sunder.Package.Agent.Mcp.Services;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Package.Agent.Models;
using AgentCore::Sunder.Package.Agent.PackageViews;
using AnthropicWorker::Sunder.Package.Agent.Provider.Anthropic;
using GeminiWorker::Sunder.Package.Agent.Provider.Gemini;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using SkillsApp::Sunder.Package.Agent.Skills.PackageViews;
using SkillsWorker::Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Storage;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Models;
using SubagentsApp::Sunder.Package.Agent.Subagents.PackageViews;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Services;
using FilesWorker::Sunder.Package.Agent.Tools.Files;
using ShellWorker::Sunder.Package.Agent.Tools.Shell;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;
using AgentPresentationDispatcher = AgentCore::Sunder.Package.Agent.Shared.Presentation.IPresentationDispatcher;
using AgentChatSelectionStateService = AgentCore::Sunder.Package.Agent.Services.AgentChatSelectionStateService;
using AgentToolPresentationService = AgentCore::Sunder.Package.Agent.Services.AgentToolPresentationService;
using AppRuntime = AgentCore::Sunder.Package.Agent.Runtime;
using AppModels = AgentCore::Sunder.Package.Agent.Contracts.Models;
using AnthropicReasoningEffort = AnthropicWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentReasoningEffort;
using GeminiReasoningEffort = GeminiWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentReasoningEffort;

namespace Sunder.Package.Agent.Tests;

extern alias McpApp;
using AgentMcpSettingsViewModel = McpApp::Sunder.Package.Agent.Mcp.AgentMcpSettingsViewModel;
using McpStatusKind = McpApp::Sunder.Package.Agent.Mcp.McpStatusKind;

public sealed partial class AgentRunCoordinatorTests
{

    [Fact]
    public async Task AgentProfilesViewModel_CompactLayout_UsesRowActivationAndReturnsToList()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        var profile = await runtime.ProfileService.CreateProfileAsync("Alpha Profile");
        using var viewModel = RegressionTestAppViews.Profiles(runtime.ProfileService);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedProfile?.ProfileId == profile.ProfileId);

        viewModel.IsCompactLayout = true;

        Assert.Null(viewModel.SelectedProfile);
        Assert.False(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowCompactList);

        viewModel.ActivateProfile(
            viewModel.Profiles.Single(item => item.ProfileId == profile.ProfileId)
        );

        Assert.True(viewModel.IsEditorActive);
        Assert.Equal(profile.ProfileId, viewModel.SelectedProfile?.ProfileId);
        Assert.True(viewModel.ShowCompactEditor);

        viewModel.BackToProfileListCommand.Execute(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Null(viewModel.SelectedProfile);
        Assert.True(viewModel.ShowCompactList);

        viewModel.IsCompactLayout = false;

        Assert.Null(viewModel.SelectedProfile);
        Assert.True(viewModel.ShowListPane);
        Assert.True(viewModel.ShowEditorPane);
    }

    [Fact]
    public async Task AgentProfilesViewModel_SaveAndDelete_CompactLayout_ReturnToListAndClearSelection()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        var profile = await runtime.ProfileService.CreateProfileAsync("Alpha Profile");
        using var viewModel = RegressionTestAppViews.Profiles(runtime.ProfileService);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedProfile?.ProfileId == profile.ProfileId);
        viewModel.IsCompactLayout = true;
        viewModel.ActivateProfile(
            viewModel.Profiles.Single(item => item.ProfileId == profile.ProfileId)
        );
        await WaitUntilAsync(() => viewModel.DisplayName == "Alpha Profile" && !viewModel.IsBusy);
        viewModel.DisplayName = "Saved Profile";

        await viewModel.SaveProfileCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Null(viewModel.SelectedProfile);
        Assert.Empty(viewModel.StatusText);
        Assert.False(viewModel.HasStatusText);
        Assert.Equal(
            "Saved Profile",
            runtime.ProfileService.GetProfile(profile.ProfileId)?.DisplayName
        );

        viewModel.ActivateProfile(
            viewModel.Profiles.Single(item => item.ProfileId == profile.ProfileId)
        );
        await WaitUntilAsync(
            () => viewModel.SelectedProfile?.ProfileId == profile.ProfileId && !viewModel.IsBusy
        );

        await viewModel.DeleteProfileCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Null(viewModel.SelectedProfile);
        Assert.Empty(viewModel.Profiles);
        Assert.Null(runtime.ProfileService.GetProfile(profile.ProfileId));
    }

    [Fact]
    public async Task AgentProfilesViewModel_Save_WideLayout_KeepsEditorLoadedAndAutoClearsSuccess()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        var profile = await runtime.ProfileService.CreateProfileAsync("Alpha Profile");
        using var viewModel = RegressionTestAppViews.Profiles(runtime.ProfileService);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() =>
            viewModel.SelectedProfile?.ProfileId == profile.ProfileId && !viewModel.IsBusy);
        viewModel.Profiles.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                viewModel.SelectedProfile = null;
            }
        };
        viewModel.DisplayName = "Saved Profile";

        await viewModel.SaveProfileCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Equal(profile.ProfileId, viewModel.SelectedProfile?.ProfileId);
        Assert.Equal("Saved Profile", viewModel.DisplayName);
        Assert.Equal(
            "Saved Profile",
            runtime.ProfileService.GetProfile(profile.ProfileId)?.DisplayName
        );
        Assert.Equal("Profile saved.", viewModel.StatusText);
        Assert.True(viewModel.HasStatusText);
        Assert.True(viewModel.IsStatusSuccess);
        Assert.False(viewModel.IsStatusWarning);
        Assert.False(viewModel.IsStatusError);

        await WaitUntilAsync(() => !viewModel.HasStatusText, TimeSpan.FromSeconds(4));

        Assert.Empty(viewModel.StatusText);
        Assert.Equal(AgentProfileStatusKind.None, viewModel.StatusKind);
    }

    [Fact]
    public async Task AgentProfilesViewModel_NoChatProviders_ShowsEmptyStateAndReloadsProviderList()
    {
        using var runtime = AgentTestRuntime.CreateWithoutChatProvider();
        var profile = await runtime.ProfileService.CreateProfileAsync("Alpha Profile");
        using var viewModel = RegressionTestAppViews.Profiles(runtime.ProfileService);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(
            () => viewModel.SelectedProfile?.ProfileId == profile.ProfileId && !viewModel.IsBusy
        );

        Assert.True(viewModel.HasNoChatProviders);
        Assert.False(viewModel.ShowChatProviderPicker);
        Assert.False(viewModel.ShowChatModelSelection);
        Assert.False(viewModel.ShowReasoningOptions);

        runtime.ExtensionCatalog.AddProvider(
            RegressionTestBindings.ChatProviders,
            new ScriptedProvider((_, _) => Complete("done"))
        );
        await viewModel.ReloadProfileProvidersCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasChatProviders);
        Assert.True(viewModel.ShowChatProviderPicker);
        Assert.True(viewModel.ShowChatModelSelection);
    }

    [Fact]
    public async Task AgentProfilesViewModel_NonReadyChatProvider_ShowsSettingsWarningAndHidesModelControls()
    {
        var settingsNavigation = new CapturingPackageSettingsNavigationService();
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider(
                (_, _) => Complete("done"),
                readinessStatus: AgentProviderReadinessStatus.NeedsConfiguration,
                readinessMessage: "Authorize Test Provider.",
                packageId: "test.provider"
            )
        );
        var profile = await runtime.ProfileService.CreateProfileAsync("Alpha Profile");
        using var viewModel = RegressionTestAppViews.Profiles(
            runtime.ProfileService,
            settingsNavigation
        );
        await viewModel.InitializeAsync();
        await WaitUntilAsync(
            () => viewModel.SelectedProfile?.ProfileId == profile.ProfileId && !viewModel.IsBusy
        );

        Assert.True(viewModel.ShowChatProviderWarning);
        Assert.Equal("Authorize Test Provider.", viewModel.ChatProviderWarningText);
        Assert.False(viewModel.ShowChatModelSelection);
        Assert.False(viewModel.ShowReasoningOptions);
        Assert.True(viewModel.CanOpenChatProviderSettings);

        await viewModel.OpenSelectedChatProviderSettingsCommand.ExecuteAsync(null);

        Assert.Equal("test.provider", settingsNavigation.OpenedPackageId);
        Assert.Empty(viewModel.StatusText);
        Assert.False(viewModel.HasStatusText);
    }

    [Fact]
    public async Task AgentProfilesViewModel_EmbeddingsDisabled_HidesEmbeddingModel()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        runtime.ExtensionCatalog.AddProvider(
            RegressionTestBindings.ProfileCapabilityConsumers,
            new TestEmbeddingCapabilityConsumer()
        );
        runtime.AddEmbeddingProvider(new TestEmbeddingProvider("test-embeddings"));
        var profile = await runtime.ProfileService.CreateProfileAsync("Alpha Profile");
        using var viewModel = RegressionTestAppViews.Profiles(runtime.ProfileService);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(
            () => viewModel.SelectedProfile?.ProfileId == profile.ProfileId && !viewModel.IsBusy
        );

        Assert.True(viewModel.ShowEmbeddingsSection);
        Assert.True(viewModel.ShowEmbeddingProviderPicker);
        Assert.Null(viewModel.SelectedEmbeddingProvider?.Id);
        Assert.False(viewModel.ShowEmbeddingModelSelection);
    }

    [Fact]
    public async Task AgentProfilesViewModel_NonReadyEmbeddingProvider_ShowsSettingsWarningAndHidesModel()
    {
        var settingsNavigation = new CapturingPackageSettingsNavigationService();
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        runtime.ExtensionCatalog.AddProvider(
            RegressionTestBindings.ProfileCapabilityConsumers,
            new TestEmbeddingCapabilityConsumer()
        );
        runtime.AddEmbeddingProvider(
            new TestEmbeddingProvider(
                "test-embeddings",
                readinessStatus: AgentProviderReadinessStatus.NeedsConfiguration,
                readinessMessage: "Configure Test Embeddings.",
                packageId: "test.embeddings"
            )
        );
        var profile = await runtime.ProfileService.CreateProfileAsync("Alpha Profile");
        using var viewModel = RegressionTestAppViews.Profiles(
            runtime.ProfileService,
            settingsNavigation
        );
        await viewModel.InitializeAsync();
        await WaitUntilAsync(
            () => viewModel.SelectedProfile?.ProfileId == profile.ProfileId && !viewModel.IsBusy
        );

        viewModel.SelectedEmbeddingProvider = viewModel.EmbeddingProviders.Single(provider =>
            provider.Id == "test-embeddings"
        );
        await WaitUntilAsync(() => viewModel.ShowEmbeddingProviderWarning && !viewModel.IsBusy);

        Assert.Equal("Configure Test Embeddings.", viewModel.EmbeddingProviderWarningText);
        Assert.False(viewModel.ShowEmbeddingModelSelection);
        Assert.True(viewModel.CanOpenEmbeddingProviderSettings);

        await viewModel.OpenSelectedEmbeddingProviderSettingsCommand.ExecuteAsync(null);

        Assert.Equal("test.embeddings", settingsNavigation.OpenedPackageId);
        Assert.Empty(viewModel.StatusText);
        Assert.False(viewModel.HasStatusText);
    }

    [Fact]
    public async Task SubagentsViewModel_CompactLayout_UsesRowActivationAndReturnsToList()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-list-tests",
            Guid.NewGuid().ToString("N")
        );
        try
        {
            using var runtime = AgentTestRuntime.Create(
                new ScriptedProvider((_, _) => Complete("done"))
            );
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Alpha Subagent");
            using var gateway = new WorkerBackedSubagentManagementGateway(service, runtime.ExtensionCatalog);
            using var viewModel = new SubagentsViewModel(gateway)
            {
                IsCompactLayout = true,
            };
            await viewModel.InitializeAsync();

            Assert.Null(viewModel.SelectedSubagent);
            Assert.False(viewModel.IsEditorActive);
            Assert.True(viewModel.ShowCompactList);

            viewModel.ActivateSubagent(
                viewModel.Subagents.Single(item => item.SubagentId == subagent.SubagentId)
            );

            Assert.True(viewModel.IsEditorActive);
            Assert.Equal(subagent.SubagentId, viewModel.SelectedSubagent?.SubagentId);
            Assert.True(viewModel.ShowCompactEditor);

            viewModel.BackToSubagentListCommand.Execute(null);

            Assert.False(viewModel.IsEditorActive);
            Assert.Null(viewModel.SelectedSubagent);
            Assert.True(viewModel.ShowCompactList);

            viewModel.IsCompactLayout = false;

            Assert.Null(viewModel.SelectedSubagent);
            Assert.True(viewModel.ShowListPane);
            Assert.True(viewModel.ShowEditorPane);
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task SubagentsViewModel_SaveAndDelete_CompactLayout_ReturnToListAndClearSelection()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-list-tests",
            Guid.NewGuid().ToString("N")
        );
        try
        {
            using var runtime = AgentTestRuntime.Create(
                new ScriptedProvider((_, _) => Complete("done"))
            );
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Alpha Subagent");
            using var gateway = new WorkerBackedSubagentManagementGateway(service, runtime.ExtensionCatalog);
            using var viewModel = new SubagentsViewModel(gateway)
            {
                IsCompactLayout = true,
            };
            await viewModel.InitializeAsync();
            viewModel.ActivateSubagent(
                viewModel.Subagents.Single(item => item.SubagentId == subagent.SubagentId)
            );
            viewModel.Description = "Handles focused tasks.";

            await viewModel.SaveSubagentCommand.ExecuteAsync(null);

            Assert.False(viewModel.IsEditorActive);
            Assert.Null(viewModel.SelectedSubagent);
            Assert.Empty(viewModel.StatusText);
            Assert.False(viewModel.HasStatusText);
            Assert.Equal(
                "Handles focused tasks.",
                service.GetSubagent(subagent.SubagentId)?.Description
            );

            viewModel.ActivateSubagent(
                viewModel.Subagents.Single(item => item.SubagentId == subagent.SubagentId)
            );

            await viewModel.DeleteSubagentCommand.ExecuteAsync(null);

            Assert.False(viewModel.IsEditorActive);
            Assert.Null(viewModel.SelectedSubagent);
            Assert.Empty(viewModel.Subagents);
            Assert.Null(service.GetSubagent(subagent.SubagentId));
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task SubagentsViewModel_Save_WideLayout_KeepsEditorLoadedAndAutoClearsSuccess()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-list-tests",
            Guid.NewGuid().ToString("N")
        );
        try
        {
            using var runtime = AgentTestRuntime.Create(
                new ScriptedProvider((_, _) => Complete("done"))
            );
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Alpha Subagent");
            using var gateway = new WorkerBackedSubagentManagementGateway(service, runtime.ExtensionCatalog);
            using var viewModel = new SubagentsViewModel(gateway);
            await viewModel.InitializeAsync();
            Assert.Equal(subagent.SubagentId, viewModel.SelectedSubagent?.SubagentId);
            viewModel.Subagents.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Reset)
                {
                    viewModel.SelectedSubagent = null;
                }
            };
            viewModel.Description = "Handles focused tasks.";

            await viewModel.SaveSubagentCommand.ExecuteAsync(null);

            Assert.False(viewModel.IsEditorActive);
            Assert.Equal(subagent.SubagentId, viewModel.SelectedSubagent?.SubagentId);
            Assert.Equal("Handles focused tasks.", viewModel.Description);
            Assert.Equal(
                "Handles focused tasks.",
                service.GetSubagent(subagent.SubagentId)?.Description
            );
            Assert.Equal("Subagent saved.", viewModel.StatusText);
            Assert.True(viewModel.HasStatusText);
            Assert.True(viewModel.IsStatusSuccess);
            Assert.False(viewModel.IsStatusWarning);
            Assert.False(viewModel.IsStatusError);

            await WaitUntilAsync(() => !viewModel.HasStatusText, TimeSpan.FromSeconds(4));

            Assert.Empty(viewModel.StatusText);
            Assert.Equal(SubagentStatusKind.None, viewModel.StatusKind);
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task SubagentsViewModel_NoChatProviders_ShowsEmptyStateAndReloadsProviderList()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-provider-tests",
            Guid.NewGuid().ToString("N")
        );
        try
        {
            using var runtime = AgentTestRuntime.CreateWithoutChatProvider();
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Alpha Subagent");
            using var gateway = new WorkerBackedSubagentManagementGateway(service, runtime.ExtensionCatalog);
            using var viewModel = new SubagentsViewModel(gateway);
            await viewModel.InitializeAsync();

            Assert.Equal(subagent.SubagentId, viewModel.SelectedSubagent?.SubagentId);
            Assert.True(viewModel.HasNoChatProviderChoices);
            Assert.False(viewModel.ShowChatProviderPicker);
            Assert.False(viewModel.ShowChatModelSelection);
            Assert.False(viewModel.ShowReasoningOptions);

            runtime.ExtensionCatalog.AddProvider(
                RegressionTestBindings.ChatProviders,
                new ScriptedProvider((_, _) => Complete("done"))
            );
            await viewModel.ReloadSubagentChatProvidersCommand.ExecuteAsync(null);

            Assert.True(viewModel.HasChatProviderChoices, viewModel.StatusText);
            Assert.True(viewModel.ShowChatProviderPicker);
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task SubagentsViewModel_NonReadyChatProvider_ShowsSettingsWarningAndHidesModelControls()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-provider-tests",
            Guid.NewGuid().ToString("N")
        );
        try
        {
            var settingsNavigation = new CapturingPackageSettingsNavigationService();
            using var runtime = AgentTestRuntime.Create(
                new ScriptedProvider(
                    (_, _) => Complete("done"),
                    readinessStatus: AgentProviderReadinessStatus.NeedsConfiguration,
                    readinessMessage: "Authorize Test Provider.",
                    packageId: "test.provider"
                )
            );
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            service.CreateSubagent("Alpha Subagent");
            using var gateway = new WorkerBackedSubagentManagementGateway(service, runtime.ExtensionCatalog);
            using var viewModel = new SubagentsViewModel(
                gateway,
                settingsNavigation
            );
            await viewModel.InitializeAsync();

            viewModel.SelectedChatProvider = viewModel.ChatProviders.Single(provider =>
                provider.ProviderId == "test-provider"
            );
            await WaitUntilAsync(() => viewModel.ShowChatProviderWarning);

            Assert.Equal("Authorize Test Provider.", viewModel.ChatProviderWarningText);
            Assert.False(viewModel.ShowChatModelSelection);
            Assert.False(viewModel.ShowReasoningOptions);
            Assert.True(viewModel.CanOpenChatProviderSettings);

            await viewModel.OpenSelectedChatProviderSettingsCommand.ExecuteAsync(null);

            Assert.Equal("test.provider", settingsNavigation.OpenedPackageId);
            Assert.Empty(viewModel.StatusText);
            Assert.False(viewModel.HasStatusText);
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task SubsessionsViewModel_CompactLayout_UsesRowActivationAndReturnsToList()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );
        using var runtimeAdapter = WorkerBackedSubagentManagementGateway.ForRuntime(
            new TestPackageContext(runtime.RootPath), runtime.ExtensionCatalog);
        using var viewModel = new SubsessionsViewModel(
            runtimeAdapter,
            runtimeAdapter,
            runtimeAdapter,
            runtimeAdapter)
        {
            IsCompactLayout = true,
        };
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Subsessions.Any(item => item.SessionId == childSession.SessionId));

        Assert.Null(viewModel.SelectedSubsession);
        Assert.False(viewModel.IsDetailActive);
        Assert.True(viewModel.ShowCompactList);

        Assert.False(viewModel.HasLoadError, viewModel.StatusText);
        viewModel.ActivateSubsession(
            viewModel.Subsessions.Single(item => item.SessionId == childSession.SessionId)
        );

        Assert.True(viewModel.IsDetailActive);
        Assert.Equal(childSession.SessionId, viewModel.SelectedSubsession?.SessionId);
        Assert.True(viewModel.ShowCompactDetail);

        viewModel.BackToSubsessionsListCommand.Execute(null);

        Assert.False(viewModel.IsDetailActive);
        Assert.Null(viewModel.SelectedSubsession);
        Assert.True(viewModel.ShowCompactList);

        viewModel.IsCompactLayout = false;

        Assert.Null(viewModel.SelectedSubsession);
        Assert.True(viewModel.ShowListPane);
        Assert.True(viewModel.ShowDetailPane);
    }

    [Fact]
    public async Task AgentSessionService_DeleteSession_RemovesAttachmentFilesForSessionTree()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var childSession = runtime.SessionService.CreateSession(
            "Child Session",
            parentSessionId: parentSessionId,
            rootSessionId: parentSessionId
        );
        var parentAttachment = await runtime.AttachmentService.StoreAttachmentAsync(
            parentSessionId,
            new AgentAttachmentUploadRequest(
                "parent.txt",
                "text/plain",
                Encoding.UTF8.GetBytes("parent attachment")
            )
        );
        var childAttachment = await runtime.AttachmentService.StoreAttachmentAsync(
            childSession.SessionId,
            new AgentAttachmentUploadRequest(
                "child.txt",
                "text/plain",
                Encoding.UTF8.GetBytes("child attachment")
            )
        );

        Assert.NotEmpty(
            await runtime.AttachmentService.ReadAttachmentBytesAsync(parentAttachment.Metadata)
        );
        Assert.NotEmpty(
            await runtime.AttachmentService.ReadAttachmentBytesAsync(childAttachment.Metadata)
        );

        await runtime.SessionService.DeleteSessionAsync(parentSessionId);

        Assert.Null(runtime.SessionService.GetSession(parentSessionId));
        Assert.Null(runtime.SessionService.GetSession(childSession.SessionId));
        using var runtimeAdapter = WorkerBackedSubagentManagementGateway.ForRuntime(
            new TestPackageContext(runtime.RootPath), runtime.ExtensionCatalog);
        using (var subsessionsViewModel = new SubsessionsViewModel(
                   runtimeAdapter,
                   runtimeAdapter,
                   runtimeAdapter,
                   runtimeAdapter))
        {
            Assert.DoesNotContain(
                subsessionsViewModel.Subsessions,
                session => session.SessionId == childSession.SessionId
            );
        }
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => runtime.AttachmentService.ReadAttachmentBytesAsync(parentAttachment.Metadata)
        );
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => runtime.AttachmentService.ReadAttachmentBytesAsync(childAttachment.Metadata)
        );
    }

    [Fact]
    public async Task AgentWorkspaceService_DeleteWorkspace_RemovesSessionTreeAndExternalSessionData()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var childSession = runtime.SessionService.CreateSession(
            "Child Session",
            parentSessionId: parentSessionId,
            rootSessionId: parentSessionId
        );
        var parentAttachment = await runtime.AttachmentService.StoreAttachmentAsync(
            parentSessionId,
            new AgentAttachmentUploadRequest(
                "parent.txt",
                "text/plain",
                Encoding.UTF8.GetBytes("parent attachment")
            )
        );
        var childAttachment = await runtime.AttachmentService.StoreAttachmentAsync(
            childSession.SessionId,
            new AgentAttachmentUploadRequest(
                "child.txt",
                "text/plain",
                Encoding.UTF8.GetBytes("child attachment")
            )
        );

        await runtime.WorkspaceService.DeleteWorkspaceAsync(runtime.CurrentWorkspaceId);

        Assert.Null(runtime.WorkspaceService.GetWorkspace(runtime.CurrentWorkspaceId));
        Assert.Null(runtime.SessionService.GetSession(parentSessionId));
        Assert.Null(runtime.SessionService.GetSession(childSession.SessionId));
        using var runtimeAdapter = WorkerBackedSubagentManagementGateway.ForRuntime(
            new TestPackageContext(runtime.RootPath), runtime.ExtensionCatalog);
        using (var subsessionsViewModel = new SubsessionsViewModel(
                   runtimeAdapter,
                   runtimeAdapter,
                   runtimeAdapter,
                   runtimeAdapter))
        {
            Assert.DoesNotContain(
                subsessionsViewModel.Subsessions,
                session => session.SessionId == childSession.SessionId
            );
        }
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => runtime.AttachmentService.ReadAttachmentBytesAsync(parentAttachment.Metadata)
        );
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => runtime.AttachmentService.ReadAttachmentBytesAsync(childAttachment.Metadata)
        );
    }

    [Fact]
    public async Task AgentSessionService_DeleteSession_RemovesSemanticMemoryForSessionTree()
    {
        const string toolId = "fetch_page";
        const string providerId = "test-embeddings";
        const string modelId = "semantic-v1";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var childSession = runtime.SessionService.CreateSession(
            "Child Session",
            parentSessionId: parentSessionId,
            rootSessionId: parentSessionId
        );
        var otherWorkspace = runtime.WorkspaceService.CreateWorkspace("Other Workspace");
        var otherSession = runtime.SessionService.CreateSession("Other Session", workspaceId: otherWorkspace.WorkspaceId);
        var context = new TestPackageContext(
            Path.Combine(Path.GetTempPath(), "sunder-memory-tests", Guid.NewGuid().ToString("N"))
        );
        var store = new MemoryLocalStore(context);
        var feature = CreateSemanticFeature(store, runtime.ExtensionCatalog);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.DurableLifecycleObservers, feature);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.SessionCleaners, feature);

        var parentMemory = StoreMemoryWithEmbedding(
            store,
            parentSessionId,
            providerId,
            modelId,
            "Parent memory"
        );
        var childMemory = StoreMemoryWithEmbedding(
            store,
            childSession.SessionId,
            providerId,
            modelId,
            "Child memory"
        );
        var otherMemory = StoreMemoryWithEmbedding(
            store,
            otherSession.SessionId,
            providerId,
            modelId,
            "Other memory"
        );

        await runtime.SessionService.DeleteSessionAsync(parentSessionId);
        Assert.Null(store.GetMemory(parentMemory.MemoryId));
        Assert.Null(store.GetMemory(childMemory.MemoryId));
        await feature.DeleteSessionAsync(parentSessionId);
        await feature.DeleteSessionAsync(childSession.SessionId);
        await using (var lifecycleDispatcher = new AgentLifecycleDispatcher(
                         runtime.Store,
                          runtime.ExtensionCatalog.DurableLifecycleObservers))
        {
            await lifecycleDispatcher.FlushAsync();
        }

        Assert.Null(store.GetMemory(parentMemory.MemoryId));
        Assert.Null(store.GetMemory(childMemory.MemoryId));
        Assert.Empty(store.ListEvidence(parentMemory.MemoryId));
        Assert.Empty(store.ListEvidence(childMemory.MemoryId));
        Assert.Empty(store.ListEmbeddings(parentSessionId, providerId, modelId));
        Assert.Empty(store.ListEmbeddings(childSession.SessionId, providerId, modelId));
        Assert.Empty(
            store.ListMemories(parentSessionId, searchText: "memory", includeInactive: true)
        );
        Assert.Empty(
            store.ListMemories(childSession.SessionId, searchText: "memory", includeInactive: true)
        );
        Assert.NotNull(store.GetMemory(otherMemory.MemoryId));
        Assert.NotEmpty(store.ListEmbeddings(otherSession.SessionId, providerId, modelId));
    }

    [Fact]
    public async Task AgentProfilesViewModel_RefreshesSubagentCapabilities_WhenBehaviorLoopChanges()
    {
        const string toolId = "fetch_page";
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-profile-loop-gating-tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            using var runtime = AgentTestRuntime.Create(
                new ScriptedProvider((_, _) => Complete("done")),
                new TestTool(toolId)
            );
            AddSubagentBehaviorLoop(runtime.ExtensionCatalog);
            await runtime.CreateSessionAsync(toolId);
            var subagentService = new SubagentService(
                new SubagentStore(new TestPackageContext(rootPath))
            );
            var subagent = subagentService.CreateSubagent("Researcher");
            SaveSubagent(subagentService,
                subagent.SubagentId,
                subagent.DisplayName,
                "Investigates delegated research tasks.",
                subagent.Instructions,
                null,
                null,
                []
            );
            runtime.ExtensionCatalog.AddProvider(
                RegressionTestBindings.SelectableCapabilityProviders,
                runtime.ExtensionCatalog.CreateSubagentFeature(subagentService)
            );
            using var viewModel = RegressionTestAppViews.Profiles(runtime.ProfileService, null, InlineAgentPresentationDispatcher.Instance);
            await viewModel.InitializeAsync();

            await WaitUntilAsync(() => viewModel.SelectedProfile is not null && !viewModel.IsBusy);
            Assert.DoesNotContain(
                viewModel.PackageCapabilities,
                capability => capability.CapabilityId == subagent.SubagentId
            );

            await InlineAgentPresentationDispatcher.Instance.InvokeAsync(() =>
                viewModel.SelectedBehaviorLoop = viewModel.BehaviorLoops.Single(loop =>
                    loop.LoopId == SubagentConstants.OrchestratedBehaviorLoopId));

            await WaitUntilAsync(() => viewModel.PackageCapabilities.Any(capability =>
                capability.CapabilityId == subagent.SubagentId) && !viewModel.IsBusy);
            var subagentOption = viewModel.PackageCapabilities.Single(capability =>
                capability.CapabilityId == subagent.SubagentId
            );
            subagentOption.IsEnabled = true;

            viewModel.SelectedBehaviorLoop = viewModel.BehaviorLoops.Single(loop =>
                loop.LoopId == AgentBehaviorLoopIds.Default
            );

            await WaitUntilAsync(
                () =>
                    viewModel.PackageCapabilities.All(capability =>
                        capability.CapabilityId != subagent.SubagentId
                    )
                    && !viewModel.IsBusy
            );

            viewModel.SelectedBehaviorLoop = viewModel.BehaviorLoops.Single(loop =>
                loop.LoopId == SubagentConstants.OrchestratedBehaviorLoopId
            );

            await WaitUntilAsync(
                () =>
                    viewModel.PackageCapabilities.Any(capability =>
                        capability.CapabilityId == subagent.SubagentId && capability.IsEnabled
                    )
                    && !viewModel.IsBusy
            );
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public async Task AgentProfilesViewModel_RefreshesSubagentCapabilities_WhenSubagentChanges()
    {
        const string toolId = "fetch_page";
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-profile-refresh-tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            using var runtime = AgentTestRuntime.Create(
                new ScriptedProvider((_, _) => Complete("done")),
                new TestTool(toolId)
            );
            AddSubagentBehaviorLoop(runtime.ExtensionCatalog);
            await runtime.CreateSessionAsync(toolId);
            SaveProfileBehaviorLoop(
                runtime.ProfileService,
                runtime.CurrentProfile,
                SubagentConstants.OrchestratedBehaviorLoopId
            );
            var subagentService = new SubagentService(
                new SubagentStore(new TestPackageContext(rootPath))
            );
            var incomplete = subagentService.CreateSubagent("Draft Specialist");
            var usable = subagentService.CreateSubagent("Researcher");
            SaveSubagent(subagentService,
                usable.SubagentId,
                usable.DisplayName,
                "Investigates delegated research tasks.",
                usable.Instructions,
                null,
                null,
                []
            );
            runtime.ExtensionCatalog.AddProvider(
                RegressionTestBindings.SelectableCapabilityProviders,
                runtime.ExtensionCatalog.CreateSubagentFeature(subagentService)
            );
            using var viewModel = RegressionTestAppViews.Profiles(runtime.ProfileService);
            await viewModel.InitializeAsync();

            await WaitUntilAsync(
                () =>
                    viewModel.PackageCapabilities.Any(capability =>
                        capability.CapabilityId == incomplete.SubagentId
                    )
                    && viewModel.PackageCapabilities.Any(capability =>
                        capability.CapabilityId == usable.SubagentId
                    )
            );
            var incompleteOption = viewModel.PackageCapabilities.Single(capability =>
                capability.CapabilityId == incomplete.SubagentId
            );
            var usableOption = viewModel.PackageCapabilities.Single(capability =>
                capability.CapabilityId == usable.SubagentId
            );
            Assert.False(incompleteOption.CanSelect);
            Assert.False(incompleteOption.IsEnabled);
            Assert.Contains(
                "Description is required",
                incompleteOption.StatusText,
                StringComparison.OrdinalIgnoreCase
            );
            usableOption.IsEnabled = true;

            SaveSubagent(subagentService,
                incomplete.SubagentId,
                incomplete.DisplayName,
                "Handles focused delegated tasks.",
                incomplete.Instructions,
                null,
                null,
                []
            );

            await WaitUntilAsync(
                () =>
                    viewModel
                        .PackageCapabilities.Single(capability =>
                            capability.CapabilityId == incomplete.SubagentId
                        )
                        .CanSelect
            );
            incompleteOption = viewModel.PackageCapabilities.Single(capability =>
                capability.CapabilityId == incomplete.SubagentId
            );
            usableOption = viewModel.PackageCapabilities.Single(capability =>
                capability.CapabilityId == usable.SubagentId
            );
            Assert.True(incompleteOption.CanSelect);
            Assert.True(usableOption.IsEnabled);

            SaveSubagent(subagentService,
                usable.SubagentId,
                "Lead Researcher",
                "Investigates delegated research tasks.",
                usable.Instructions,
                null,
                null,
                []
            );

            await WaitUntilAsync(
                () =>
                    viewModel.PackageCapabilities.Any(capability =>
                        capability.CapabilityId == usable.SubagentId
                        && capability.DisplayName == "Lead Researcher"
                    )
            );
            usableOption = viewModel.PackageCapabilities.Single(capability =>
                capability.CapabilityId == usable.SubagentId
            );
            Assert.True(usableOption.IsEnabled);
            Assert.Contains(viewModel.CapabilityGroups, group => group.Title == "Subagents");

            subagentService.DeleteSubagent(incomplete.SubagentId);

            await WaitUntilAsync(
                () =>
                    viewModel.PackageCapabilities.All(capability =>
                        capability.CapabilityId != incomplete.SubagentId
                    )
            );
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public async Task AgentProfilesViewModel_RefreshesMcpCapabilities_WhenServerCatalogChanges()
    {
        const string toolId = "fetch_page";
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-mcp-profile-refresh-tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            using var runtime = AgentTestRuntime.Create(
                new ScriptedProvider((_, _) => Complete("done")),
                new TestTool(toolId)
            );
            await runtime.CreateSessionAsync(toolId);
            var serverCatalogService = new McpServerCatalogService(
                new TestPackageContext(rootPath)
            );
            await using var connectionManager = new McpClientConnectionManager(
                NullLoggerFactory.Instance
            );
            runtime.ExtensionCatalog.AddProvider(
                RegressionTestBindings.SelectableCapabilityProviders,
                new McpToolSource(serverCatalogService, connectionManager)
            );
            using var viewModel = RegressionTestAppViews.Profiles(runtime.ProfileService);
            await viewModel.InitializeAsync();

            await WaitUntilAsync(() => viewModel.SelectedProfile is not null);
            Assert.Empty(viewModel.PackageCapabilities);

            var server = new ConfiguredMcpServerRecord
            {
                ServerId = "local-mcp",
                Name = "local_mcp",
                DisplayName = "Local MCP",
                Description = "Local MCP tools.",
                IsEnabled = true,
                TransportType = ConfiguredMcpTransportType.Stdio,
                CommandParts = ["node", "server.js"],
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            await serverCatalogService.SaveServerAsync(
                server,
                new Dictionary<string, string>(),
                new Dictionary<string, string>()
            );

            await WaitUntilAsync(
                () =>
                    viewModel.PackageCapabilities.Any(capability =>
                        capability.CapabilityId == server.ServerId
                    )
            );
            var group = Assert.Single(viewModel.PackageCapabilityGroups);
            Assert.Equal("MCP Servers", group.Title);
            Assert.Contains(
                viewModel.CapabilityGroups,
                capabilityGroup => capabilityGroup.Title == "MCP Servers"
            );

            var renamedServer = server with
            {
                DisplayName = "Renamed MCP",
                Description = "Renamed MCP tools.",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await serverCatalogService.SaveServerAsync(
                renamedServer,
                new Dictionary<string, string>(),
                new Dictionary<string, string>()
            );

            await WaitUntilAsync(
                () =>
                    viewModel.PackageCapabilities.Any(capability =>
                        capability.CapabilityId == server.ServerId
                        && capability.DisplayName == "Renamed MCP"
                    )
            );

            await serverCatalogService.DeleteServerAsync(server.ServerId);

            await WaitUntilAsync(
                () =>
                    viewModel.PackageCapabilities.All(capability =>
                        capability.CapabilityId != server.ServerId
                    )
            );
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public async Task AgentProfilesViewModel_RefreshesLocalTools_WhenToolCatalogChanges()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        await runtime.CreateSessionAsync("dynamic_tool");
        using var viewModel = RegressionTestAppViews.Profiles(runtime.ProfileService);
        await viewModel.InitializeAsync();

        await WaitUntilAsync(() => viewModel.SelectedProfile is not null);
        Assert.Empty(viewModel.LocalTools);

        runtime.ExtensionCatalog.AddTool(new MetadataTool("dynamic_tool", "Dynamic Tools")
        );

        await WaitUntilAsync(
            () => viewModel.LocalTools.Any(tool => tool.CapabilityId == "dynamic_tool")
        );
        var group = Assert.Single(viewModel.LocalToolGroups);
        Assert.Equal("Dynamic Tools", group.Title);
        Assert.Contains(
            viewModel.CapabilityGroups,
            capabilityGroup => capabilityGroup.Title == "Dynamic Tools"
        );
    }

    [Fact]
    public async Task AgentProfilesViewModel_RefreshesCapabilities_WhenProviderIsAddedAfterProfileOpen()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-profile-provider-added-refresh-tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            using var runtime = AgentTestRuntime.Create(
                new ScriptedProvider((_, _) => Complete("done"))
            );
            AddSubagentBehaviorLoop(runtime.ExtensionCatalog);
            await runtime.CreateSessionAsync("test_tool");
            SaveProfileBehaviorLoop(
                runtime.ProfileService,
                runtime.CurrentProfile,
                SubagentConstants.OrchestratedBehaviorLoopId
            );
            var subagentService = new SubagentService(
                new SubagentStore(new TestPackageContext(rootPath))
            );
            var subagent = subagentService.CreateSubagent("Late Specialist");
            SaveSubagent(subagentService,
                subagent.SubagentId,
                subagent.DisplayName,
                "Handles work after the profile view is already open.",
                subagent.Instructions,
                null,
                null,
                []
            );
            using var viewModel = RegressionTestAppViews.Profiles(runtime.ProfileService);
            await viewModel.InitializeAsync();

            await WaitUntilAsync(() => viewModel.SelectedProfile is not null);
            Assert.Empty(viewModel.PackageCapabilities);

            runtime.ExtensionCatalog.AddProvider(
                RegressionTestBindings.SelectableCapabilityProviders,
                runtime.ExtensionCatalog.CreateSubagentFeature(subagentService)
            );

            await WaitUntilAsync(
                () =>
                    viewModel.PackageCapabilities.Any(capability =>
                        capability.CapabilityId == subagent.SubagentId
                    )
            );
            Assert.Contains(viewModel.CapabilityGroups, group => group.Title == "Subagents");
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public async Task AgentProfileService_UnsubscribesFromCapabilityProvidersWhenDisposed()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var provider = new MutableSelectableCapabilityProvider();
        var store = new AgentLocalStore(scope.Context);
        var sessions = new AgentSessionService(store, catalog.SessionCleaners);
        var workspaces = new AgentWorkspaceService(store, catalog.SessionCleaners, sessions);
        var tools = new AgentToolService(
            sessions,
            workspaces,
            new AgentExecutionTargetService(catalog.ExecutionTargets),
            catalog.ToolSources,
            catalog.ObservedToolExecutions);
        await using var profiles = catalog.CreateProfileService(store, tools);
        var changeCount = 0;
        profiles.SelectableCapabilitiesChanged += () => Interlocked.Increment(ref changeCount);
        _ = await profiles.ListSelectableProfileCapabilitiesAsync();
        catalog.AddProvider(RegressionTestBindings.SelectableCapabilityProviders, provider);
        await WaitUntilAsync(() => Volatile.Read(ref changeCount) > 0);
        await provider.SubscriptionStarted.WaitAsync(TimeSpan.FromSeconds(2));

        var baseline = Volatile.Read(ref changeCount);
        provider.RaiseChanged();
        await WaitUntilAsync(() => Volatile.Read(ref changeCount) > baseline);

        profiles.Dispose();
        var disposedBaseline = Volatile.Read(ref changeCount);
        provider.RaiseChanged();
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Equal(disposedBaseline, Volatile.Read(ref changeCount));
    }

    [Fact]
    public async Task SubagentsViewModel_RefreshesMcpCapabilities_WhenServerCatalogChanges()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-mcp-subagent-refresh-tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            using var runtime = AgentTestRuntime.Create(
                new ScriptedProvider((_, _) => Complete("done"))
            );
            var subagentService = new SubagentService(
                new SubagentStore(new TestPackageContext(Path.Combine(rootPath, "subagents")))
            );
            var subagent = subagentService.CreateSubagent("Worker");
            SaveSubagent(subagentService,
                subagent.SubagentId,
                subagent.DisplayName,
                "Handles delegated work.",
                subagent.Instructions,
                null,
                null,
                []
            );
            var serverCatalogService = new McpServerCatalogService(
                new TestPackageContext(Path.Combine(rootPath, "mcp"))
            );
            await using var connectionManager = new McpClientConnectionManager(
                NullLoggerFactory.Instance
            );
            runtime.ExtensionCatalog.AddProvider(
                RegressionTestBindings.SelectableCapabilityProviders,
                new McpToolSource(serverCatalogService, connectionManager)
            );
            using var gateway = new WorkerBackedSubagentManagementGateway(
                subagentService,
                runtime.ExtensionCatalog);
            using var viewModel = new SubagentsViewModel(gateway);
            await viewModel.InitializeAsync();

            await WaitUntilAsync(() => viewModel.SelectedSubagent is not null);
            Assert.DoesNotContain(
                viewModel.CapabilityOptions,
                capability => capability.SourceId == "mcp"
            );

            var server = new ConfiguredMcpServerRecord
            {
                ServerId = "local-mcp",
                Name = "local_mcp",
                DisplayName = "Local MCP",
                Description = "Local MCP tools.",
                IsEnabled = true,
                TransportType = ConfiguredMcpTransportType.Stdio,
                CommandParts = ["node", "server.js"],
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            await serverCatalogService.SaveServerAsync(
                server,
                new Dictionary<string, string>(),
                new Dictionary<string, string>()
            );

            await WaitUntilAsync(
                () =>
                    viewModel.CapabilityOptions.Any(capability =>
                        capability.CapabilityId == server.ServerId
                    )
                    && viewModel.CapabilityGroups.Any(group => group.Title == "MCP Servers")
            );
            Assert.Contains(viewModel.CapabilityGroups, group => group.Title == "MCP Servers");

            var renamedServer = server with
            {
                DisplayName = "Renamed MCP",
                Description = "Renamed MCP tools.",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await serverCatalogService.SaveServerAsync(
                renamedServer,
                new Dictionary<string, string>(),
                new Dictionary<string, string>()
            );

            await WaitUntilAsync(
                () =>
                    viewModel.CapabilityOptions.Any(capability =>
                        capability.CapabilityId == server.ServerId
                        && capability.DisplayName == "Renamed MCP"
                    )
            );

            await serverCatalogService.DeleteServerAsync(server.ServerId);

            await WaitUntilAsync(
                () =>
                    viewModel.CapabilityOptions.All(capability =>
                        capability.CapabilityId != server.ServerId
                    )
            );
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public async Task AgentMcpSettingsViewModel_CompactLayout_UsesRowActivationAndReturnsToList()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-mcp-settings-tests",
            Guid.NewGuid().ToString("N")
        );
        try
        {
            var serverCatalogService = new McpServerCatalogService(
                new TestPackageContext(rootPath)
            );
            await serverCatalogService.SaveServerAsync(
                CreateMcpServer("local-mcp", "local_mcp", "Local MCP"),
                new Dictionary<string, string>(),
                new Dictionary<string, string>()
            );
            await using var connectionManager = new McpClientConnectionManager(
                NullLoggerFactory.Instance
            );
            await using var viewModelScope = await CreateMcpSettingsViewModel(
                serverCatalogService,
                connectionManager
            );
            var viewModel = viewModelScope.ViewModel;
            viewModel.IsCompactLayout = true;

            await viewModel.InitializeAsync();
            Assert.True(viewModel.Servers.Count == 1, viewModel.StatusText);

            Assert.Null(viewModel.SelectedServer);
            Assert.False(viewModel.IsEditorActive);
            Assert.True(viewModel.ShowCompactList);

            viewModel.ActivateServer(
                viewModel.Servers.Single(server => server.ServerId == "local-mcp")
            );
            await WaitUntilAsync(() => viewModel.Name == "local_mcp");

            Assert.True(viewModel.IsEditorActive);
            Assert.Equal("local-mcp", viewModel.SelectedServer?.ServerId);
            Assert.True(viewModel.ShowCompactEditor);

            viewModel.BackToServerListCommand.Execute(null);

            Assert.False(viewModel.IsEditorActive);
            Assert.Null(viewModel.SelectedServer);
            Assert.True(viewModel.ShowCompactList);

            viewModel.IsCompactLayout = false;

            Assert.Null(viewModel.SelectedServer);
            Assert.True(viewModel.ShowListPane);
            Assert.True(viewModel.ShowEditorPane);
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task AgentMcpSettingsViewModel_Save_CompactLayout_ReturnsToListAndClearSelection()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-mcp-settings-tests",
            Guid.NewGuid().ToString("N")
        );
        try
        {
            var serverCatalogService = new McpServerCatalogService(
                new TestPackageContext(rootPath)
            );
            await serverCatalogService.SaveServerAsync(
                CreateMcpServer("local-mcp", "local_mcp", "Local MCP"),
                new Dictionary<string, string>(),
                new Dictionary<string, string>()
            );
            await using var connectionManager = new McpClientConnectionManager(
                NullLoggerFactory.Instance
            );
            await using var viewModelScope = await CreateMcpSettingsViewModel(
                serverCatalogService,
                connectionManager
            );
            var viewModel = viewModelScope.ViewModel;
            viewModel.IsCompactLayout = true;
            await viewModel.InitializeAsync();
            Assert.True(viewModel.Servers.Count == 1, viewModel.StatusText);
            viewModel.ActivateServer(
                viewModel.Servers.Single(server => server.ServerId == "local-mcp")
            );
            await WaitUntilAsync(() => viewModel.Name == "local_mcp");

            await viewModel.SaveCommand.ExecuteAsync(null);

            Assert.False(viewModel.IsEditorActive);
            Assert.Null(viewModel.SelectedServer);
            Assert.Empty(viewModel.StatusText);
            Assert.Contains(
                await serverCatalogService.ListServersAsync(),
                server => server.ServerId == "local-mcp"
            );
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task AgentMcpSettingsViewModel_Save_WideLayout_KeepsEditorLoadedAndAutoClearsSuccess()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-mcp-settings-tests",
            Guid.NewGuid().ToString("N")
        );
        try
        {
            var serverCatalogService = new McpServerCatalogService(
                new TestPackageContext(rootPath)
            );
            await serverCatalogService.SaveServerAsync(
                CreateMcpServer("local-mcp", "local_mcp", "Local MCP"),
                new Dictionary<string, string>(),
                new Dictionary<string, string>()
            );
            await using var connectionManager = new McpClientConnectionManager(
                NullLoggerFactory.Instance
            );
            await using var viewModelScope = await CreateMcpSettingsViewModel(
                serverCatalogService,
                connectionManager
            );
            var viewModel = viewModelScope.ViewModel;

            await WaitUntilAsync(
                () =>
                    viewModel.SelectedServer?.ServerId == "local-mcp"
                    && viewModel.Name == "local_mcp"
            );

            await viewModel.SaveCommand.ExecuteAsync(null);

            Assert.False(viewModel.IsEditorActive);
            Assert.Equal("local-mcp", viewModel.SelectedServer?.ServerId);
            Assert.Equal("local_mcp", viewModel.Name);
            Assert.Contains("Saved MCP server", viewModel.StatusText, StringComparison.Ordinal);
            Assert.True(viewModel.IsStatusSuccess);
            Assert.False(viewModel.IsStatusWarning);
            Assert.False(viewModel.IsStatusError);

            await WaitUntilAsync(
                () => string.IsNullOrWhiteSpace(viewModel.StatusText),
                TimeSpan.FromSeconds(4)
            );

            Assert.Empty(viewModel.StatusText);
            Assert.Equal(McpStatusKind.None, viewModel.StatusKind);
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task AgentProfileService_SaveProfile_PersistsChatModelSettings()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        var profile = await runtime.ProfileService.CreateProfileAsync("Test Profile");

        runtime.ProfileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            profile.ChatProviderId,
            profile.ChatModelId,
            profile.EmbeddingProviderId,
            profile.EmbeddingModelId,
            chatModelSettingsJson: "{\"reasoningVariantId\":\"high\"}"
        );

        var chatBinding = runtime.ProfileService.GetChatBinding(profile.ProfileId);

        Assert.NotNull(chatBinding);
        using var settingsDocument = JsonDocument.Parse(chatBinding.SettingsJson!);
        Assert.Equal(
            "high",
            settingsDocument.RootElement.GetProperty("reasoningVariantId").GetString()
        );
    }

    [Fact]
    public async Task AgentProfileService_OrdersChatModelsNewestFirstAndUsesNewestRecommendedDefault()
    {
        var provider = new ScriptedProvider(
            (_, _) => Complete("done"),
            models:
            [
                new AgentModelDescriptor("old", "Old", 128_000, 4_096)
                {
                    ReleaseDate = new DateOnly(2024, 1, 1),
                },
                new AgentModelDescriptor("new-a", "New A", 128_000, 4_096)
                {
                    ReleaseDate = new DateOnly(2026, 1, 1),
                },
                new AgentModelDescriptor("new-b", "New B", 128_000, 4_096, IsRecommended: true)
                {
                    ReleaseDate = new DateOnly(2026, 1, 1),
                },
                new AgentModelDescriptor("undated", "Undated", 128_000, 4_096),
            ]);
        using var runtime = AgentTestRuntime.Create(provider);

        var models = await runtime.ProfileService.ListChatModelsAsync(provider.Descriptor.ProviderId);
        var profile = await runtime.ProfileService.CreateProfileAsync("Newest Model Profile");

        Assert.Equal(["new-a", "new-b", "old", "undated"], models.Select(model => model.ModelId));
        Assert.Equal("new-b", profile.ChatModelId);
    }

    [Fact]
    public async Task AnthropicAgentProvider_ExposesReasoningVariants_ForSupportedModels()
    {
        var rootPath = CreateTempTestRoot();
        try
        {
            var provider = new AnthropicAgentProvider(new TestPackageContext(rootPath));

            var models = await provider.ListModelsAsync();
            Assert.All(models, model => Assert.NotNull(model.ReleaseDate));

            var opus = models.Single(model => model.ModelId == "anthropic/claude-opus-4-7");
            Assert.Contains(opus.Variants ?? [], variant => variant.ReasoningEffort == AnthropicReasoningEffort.ExtraHigh);
            Assert.Contains(opus.SpeedOptions ?? [], option => option.SpeedOptionId == "fast");

            var opus48 = models.Single(model => model.ModelId == "anthropic/claude-opus-4-8");
            Assert.Contains(opus48.SpeedOptions ?? [], option => option.SpeedOptionId == "fast");
            Assert.DoesNotContain(models, model => model.ModelId.StartsWith("anthropic/claude-opus-4-1", StringComparison.Ordinal));

            var sonnet = models.Single(model => model.ModelId == "anthropic/claude-sonnet-4-6");
            Assert.Contains(sonnet.Variants ?? [], variant => variant.ReasoningEffort == AnthropicReasoningEffort.High);
            Assert.DoesNotContain(sonnet.Variants ?? [], variant => variant.ReasoningEffort == AnthropicReasoningEffort.ExtraHigh);

            var haiku = models.Single(model => model.ModelId == "anthropic/claude-haiku-4-5");
            Assert.True(haiku.Variants is null || haiku.Variants.Count == 0);
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task GeminiAgentProvider_ExposesReasoningVariants_ForThinkingModels()
    {
        var rootPath = CreateTempTestRoot();
        try
        {
            var provider = new GeminiAgentProvider(new TestPackageContext(rootPath));

            var models = await provider.ListModelsAsync();
            Assert.All(models, model => Assert.NotNull(model.ReleaseDate));

            var pro = models.Single(model => model.ModelId == "gemini/gemini-2.5-pro");
            Assert.Contains(pro.Variants ?? [], variant => variant.ReasoningEffort == GeminiReasoningEffort.High);

            var flash = models.Single(model => model.ModelId == "gemini/gemini-2.5-flash");
            Assert.Contains(flash.Variants ?? [], variant => variant.ReasoningEffort == GeminiReasoningEffort.Medium);

            Assert.Contains(models, model => model.ModelId == "gemini/gemini-3.5-flash");
            Assert.DoesNotContain(models, model => model.ModelId == "gemini/gemini-2.0-flash");
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }
}
