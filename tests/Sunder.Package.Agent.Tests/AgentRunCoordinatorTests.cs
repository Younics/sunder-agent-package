using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Contracts.Services;
using Sunder.Package.Agent.Mcp;
using Sunder.Package.Agent.Memory.Semantic;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Package.Agent.Skills.PackageViews;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.PackageViews;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Package.Agent.Tools.Files;
using Sunder.Sdk.Abstractions;
using Microsoft.Extensions.AI;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentRunCoordinatorTests
{
    [Fact]
    public async Task AgentSystemPromptComposer_ComposeAsync_RendersContributorBlocksAndToolInstructions()
    {
        var catalog = new TestExtensionCatalog();
        catalog.AddExtension(PackageExtensionPoints.SystemPromptContributors, new TestSystemPromptContributor());
        var composer = new AgentSystemPromptComposer(catalog);
        var request = BuildSystemPromptRequest(
            availableTools:
            [
                new AgentToolDescriptor(
                    "test_tool",
                    "Test Tool",
                    "Test tool.",
                    RuntimeInstructions: "Use this tool carefully.")
            ]);

        var prompt = await composer.ComposeAsync(request, "Base instructions.");

        Assert.Contains("Base instructions.", prompt);
        Assert.Contains("## Tool Runtime Context", prompt);
        Assert.Contains("Use this tool carefully.", prompt);
        Assert.Contains("## Test Contributor Block", prompt);
        Assert.Contains("Contributor-provided runtime guidance.", prompt);
    }

    [Fact]
    public async Task AgentSystemPromptComposer_ComposeAsync_RendersToolPriorityBlock_WhenPrioritiesAreMixed()
    {
        var composer = new AgentSystemPromptComposer(new TestExtensionCatalog());
        var request = BuildSystemPromptRequest(
            availableTools:
            [
                new AgentToolDescriptor("high_tool", "High Tool", "High priority tool.", Priority: AgentToolPriority.High),
                new AgentToolDescriptor("low_tool", "Low Tool", "Low priority tool.", Priority: AgentToolPriority.Low),
            ]);

        var prompt = await composer.ComposeAsync(request, null);

        Assert.Contains("## Tool Priority", prompt ?? string.Empty);
        Assert.Contains("prefer higher-priority tools first", prompt ?? string.Empty);
    }

    [Fact]
    public async Task AgentSystemPromptComposer_ComposeAsync_SkipsToolPriorityBlock_WhenPrioritiesAreUniform()
    {
        var composer = new AgentSystemPromptComposer(new TestExtensionCatalog());
        var request = BuildSystemPromptRequest(
            availableTools:
            [
                new AgentToolDescriptor("first_tool", "First Tool", "First tool."),
                new AgentToolDescriptor("second_tool", "Second Tool", "Second tool."),
            ]);

        var prompt = await composer.ComposeAsync(request, null);

        Assert.DoesNotContain("## Tool Priority", prompt ?? string.Empty);
    }

    [Fact]
    public async Task AgentSystemPromptComposer_ComposeAsync_PropagatesCancellation()
    {
        var composer = new AgentSystemPromptComposer(new TestExtensionCatalog());
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await composer.ComposeAsync(BuildSystemPromptRequest(), null, cancellationTokenSource.Token));
    }

    [Fact]
    public async Task FilesToolSource_ContributeAsync_IncludesExecutionScopeRoots()
    {
        var catalog = new TestExtensionCatalog();
        catalog.AddExtension(PackageExtensionPoints.ExecutionTargets, new TestScopedExecutionTarget());
        var files = new FilesToolSource(catalog);
        var now = DateTimeOffset.UtcNow;
        var request = BuildSystemPromptRequest(
            workspace: new AgentWorkspaceRecord("workspace", "Workspace", null, now, now),
            executionBinding: new AgentWorkspaceBindingRecord(
                "binding",
                "workspace",
                PackageExtensionPoints.ExecutionTargets.Id,
                "test-target",
                AgentWorkspaceBindingRoles.PrimaryExecutionTarget,
                IsEnabled: true,
                SortOrder: 0,
                now,
                now),
            availableTools:
            [
                new AgentToolDescriptor(
                    "glob",
                    "Glob",
                    "Find files.",
                    SourceKind: "workspace",
                    SourceId: "files",
                    SourceDisplayName: "Workspace Files")
            ]);

        var blocks = await files.ContributeAsync(request);
        var block = Assert.Single(blocks);

        Assert.Equal("Workspace File Scope", block.Title);
        Assert.Contains("C:\\Users\\micha\\Downloads\\ROZANA\\ROZANA", block.Content);
        Assert.Contains("Do not invent paths", block.Content);
    }

    [Fact]
    public async Task QueueUserMessageAsync_PassesSameRunToolResultIntoNextProviderRequest()
    {
        const string toolId = "fetch_page";

        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 => ToolRequest("call-1", toolId, "{\"url\":\"https://example.com\"}"),
            2 => AssertAndComplete(request, toolId, "call-1"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}.")
        });

        using var runtime = AgentTestRuntime.Create(provider, new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, "Fetch the page and summarize it.", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains(provider.Requests[1].Turns, turn =>
            turn.Kind == AgentTurnKind.ToolResult
            && turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolResult
                                      && item.ToolId == toolId
                                      && item.CallId == "call-1"));
    }

    [Fact]
    public async Task QueueUserMessageAsync_RetriesTransientStreamFailureAndReplacesPartialAssistantTurn()
    {
        var provider = new ScriptedProvider((_, requestIndex) => requestIndex switch
        {
            1 => [Delta("partial answer"), TransientStreamError()],
            2 => [Complete("final answer")],
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}.")
        });
        using var runtime = AgentTestRuntime.Create(provider);
        var sessionId = await runtime.CreateSessionAsync("noop");

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, "Answer after a retry.", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(2, provider.Requests.Count);
        Assert.DoesNotContain(provider.Requests[1].Turns, turn => RenderTurnText(turn).Contains("partial answer", StringComparison.Ordinal));
        var assistantTurn = Assert.Single(runtime.SessionService.ListTurns(sessionId), turn => turn.Role == AgentMessageRole.Assistant);
        Assert.Equal("final answer", RenderTurnText(assistantTurn));
    }

    [Fact]
    public async Task QueueUserMessageAsync_RetriesTransientStreamFailureAfterToolResult()
    {
        const string toolId = "fetch_page";

        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 => [ToolRequest("call-1", toolId, "{\"url\":\"https://example.com\"}")],
            2 => [Delta("partial summary"), TransientStreamError()],
            3 => [AssertAndComplete(request, toolId, "call-1")],
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}.")
        });
        using var runtime = AgentTestRuntime.Create(provider, new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, "Fetch the page and summarize it.", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Contains(provider.Requests[2].Turns, turn =>
            turn.Kind == AgentTurnKind.ToolResult
            && turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolResult
                                      && item.ToolId == toolId
                                      && item.CallId == "call-1"));
        Assert.DoesNotContain(provider.Requests[2].Turns, turn => RenderTurnText(turn).Contains("partial summary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueueUserMessageAsync_StoresTextAttachmentAndSendsExtractedContent()
    {
        var provider = new ScriptedProvider((request, requestIndex) =>
        {
            Assert.Equal(1, requestIndex);
            var userText = RenderTurnText(Assert.Single(request.Turns, turn => turn.Role == AgentMessageRole.User));
            Assert.Contains("Review this.", userText);
            Assert.Contains("Attached file: notes.md", userText);
            Assert.Contains("hello from file", userText);
            return Complete("done");
        });
        using var runtime = AgentTestRuntime.Create(provider);
        var sessionId = await runtime.CreateSessionAsync("noop");

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Review this.",
            runtime.CurrentWorkspaceId,
            [new AgentAttachmentUploadRequest("notes.md", "text/markdown", Encoding.UTF8.GetBytes("# Notes\nhello from file"))]);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        var storedUserTurn = Assert.Single(runtime.SessionService.ListTurns(sessionId), turn => turn.Role == AgentMessageRole.User);
        Assert.Contains(storedUserTurn.Items, item => item.Kind == AgentTurnItemKind.Text && item.TextContent == "Review this.");
        var attachmentItem = Assert.Single(storedUserTurn.Items, item => item.Kind == AgentTurnItemKind.Attachment);
        var metadata = JsonSerializer.Deserialize<AgentAttachmentMetadata>(attachmentItem.StructuredPayloadJson!);
        Assert.NotNull(metadata);
        Assert.Equal("notes.md", metadata.FileName);
        Assert.Equal(AgentAttachmentKind.Text, metadata.Kind);
        Assert.Contains("hello from file", attachmentItem.TextContent);
    }

    [Fact]
    public async Task QueueUserMessageAsync_FallsBackToTextForUnsupportedImageAttachment()
    {
        var provider = new ScriptedProvider((request, requestIndex) =>
        {
            Assert.Equal(1, requestIndex);
            var userText = RenderTurnText(Assert.Single(request.Turns, turn => turn.Role == AgentMessageRole.User));
            Assert.Contains("diagram.png", userText);
            Assert.Contains("does not support image input", userText);
            return Complete("done");
        });
        using var runtime = AgentTestRuntime.Create(provider);
        var sessionId = await runtime.CreateSessionAsync("noop");
        byte[] pngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x00];

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            string.Empty,
            runtime.CurrentWorkspaceId,
            [new AgentAttachmentUploadRequest("diagram.png", "image/png", pngBytes)]);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
    }

    [Theory]
    [InlineData(AgentToolResultErrorCodes.ShellNonZeroExit)]
    [InlineData(AgentToolResultErrorCodes.ShellTimeout)]
    [InlineData(AgentToolResultErrorCodes.ToolExecutionException)]
    [InlineData("path-not-found")]
    [InlineData("tool-internal-error")]
    [InlineData("web-fetch-http")]
    [InlineData("web-search-http")]
    public async Task QueueUserMessageAsync_ContinuesProviderAfterToolErrorResult(string errorCode)
    {
        const string toolId = "shell";

        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 => ToolRequest("call-1", toolId, "{\"command\":\"git status --short\"}"),
            2 => AssertErroredToolResultAndComplete(request, toolId, "call-1", errorCode),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}.")
        });
        var tool = new ErrorResultTool(toolId, errorCode);
        using var runtime = AgentTestRuntime.Create(provider, tool);
        runtime.ExtensionCatalog.AddExtension(PackageExtensionPoints.BehaviorLoops, new OrchestratedAgentBehaviorLoop(runtime.ExtensionCatalog));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var profile = runtime.CurrentProfile;
        runtime.ProfileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            profile.ChatProviderId,
            profile.ChatModelId,
            profile.EmbeddingProviderId,
            profile.EmbeddingModelId,
            selectableCapabilityAssignments:
            [
                new AgentProfileSelectableCapabilityAssignmentRecord(AgentProfileSelectableCapabilityKinds.Tool, toolId)
            ],
            behaviorLoopId: SubagentConstants.OrchestratedBehaviorLoopId);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, "Check git state.", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.Contains(runtime.SessionService.ListRecentTurns(sessionId, 20), turn =>
            turn.Kind == AgentTurnKind.ToolResult
            && turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolResult
                                      && item.ToolId == toolId
                                      && item.CallId == "call-1"
                                      && item.IsError
                                      && item.ErrorCode == errorCode));
    }

    [Fact]
    public async Task QueueUserMessageAsync_ContinuesProviderAfterToolThrows()
    {
        const string toolId = "broken_tool";

        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 => ToolRequest("call-1", toolId, "{}"),
            2 => AssertErroredToolResultAndComplete(request, toolId, "call-1", AgentToolResultErrorCodes.ToolExecutionException, "boom"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}.")
        });
        var tool = new ThrowingTool(toolId, "boom");
        using var runtime = AgentTestRuntime.Create(provider, tool);
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, "Use the broken tool.", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public async Task QueueUserMessageAsync_BoundedMemoryContext_PreservesHistoricalToolPairs()
    {
        const string toolId = "fetch_page";
        const string historicalCallId = "historical-call";
        var provider = new ScriptedProvider((request, requestIndex) =>
        {
            Assert.Equal(1, requestIndex);
            Assert.Contains(request.Turns, turn =>
                turn.Kind == AgentTurnKind.ToolCall
                && turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolCall
                                          && item.ToolId == toolId
                                          && item.CallId == historicalCallId));
            Assert.Contains(request.Turns, turn =>
                turn.Kind == AgentTurnKind.ToolResult
                && turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolResult
                                          && item.ToolId == toolId
                                          && item.CallId == historicalCallId));
            AssertNoOrphanToolResults(request);
            return Complete("done");
        });

        using var runtime = AgentTestRuntime.Create(provider, new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        for (var index = 0; index < 3; index++)
        {
            runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.User, $"older-{index}");
        }

        runtime.SessionService.AppendToolCallTurn(sessionId, AgentMessageRole.Assistant, historicalCallId, toolId, "{\"url\":\"https://example.com\"}");
        runtime.SessionService.AppendToolResultTurn(
            sessionId,
            historicalCallId,
            toolId,
            "{\"url\":\"https://example.com\"}",
            "historical result",
            "historical result",
            structuredPayloadJson: null,
            sourcesJson: null,
            wasTruncated: false,
            isError: false,
            errorCode: null,
            backendId: null);
        for (var index = 0; index < 15; index++)
        {
            runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.Assistant, $"filler-{index}");
        }

        runtime.SessionService.SaveWorkingSummary(sessionId, "Existing memory summary forces bounded historical context.");

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, "can you check what files are in ~/Downloads", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
    }

    [Fact]
    public async Task QueueUserMessageAsync_StreamsToolPreambleAndKeepsPostToolResponseSeparate()
    {
        const string toolId = "fetch_page";
        const string preamble = "I'll fetch the page now.";

        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 =>
            [
                Delta(preamble),
                ToolRequest("call-1", toolId, "{\"url\":\"https://example.com\"}")
            ],
            2 => [AssertAndComplete(request, toolId, "call-1")],
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}.")
        });

        using var runtime = AgentTestRuntime.Create(provider, new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, "Fetch the page and summarize it.", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains(provider.Requests[1].Turns, turn =>
            turn.Role == AgentMessageRole.Assistant
            && turn.Kind == AgentTurnKind.Message
            && RenderTurnText(turn).Contains(preamble, StringComparison.Ordinal));

        var assistantTexts = runtime.SessionService.ListTurns(sessionId)
            .Where(turn => turn.Role == AgentMessageRole.Assistant && turn.Kind == AgentTurnKind.Message)
            .Select(RenderTurnText)
            .ToArray();
        var preambleTurn = Assert.Single(assistantTexts, text => text.Contains(preamble, StringComparison.Ordinal));
        var finalTurn = Assert.Single(assistantTexts, text => text.Contains("Used the tool result", StringComparison.Ordinal));

        Assert.DoesNotContain("Used the tool result", preambleTurn, StringComparison.Ordinal);
        Assert.DoesNotContain(preamble, finalTurn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueUserMessageAsync_AllowsToolFromSelectableCapabilityAssignment()
    {
        const string toolId = "fetch_page";

        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 => AssertToolAvailableAndRequest(request, toolId, "call-1", "{\"url\":\"https://example.com\"}"),
            2 => AssertAndComplete(request, toolId, "call-1"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}.")
        });

        var tool = new TestTool(toolId);
        using var runtime = AgentTestRuntime.Create(provider, tool);
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
            selectableCapabilityAssignments:
            [
                new AgentProfileSelectableCapabilityAssignmentRecord(
                    AgentProfileSelectableCapabilityKinds.Tool,
                    toolId,
                    "installed-packages")
            ]);
        var workspace = runtime.WorkspaceService.CreateWorkspace("Test Workspace");
        var sessionId = runtime.SessionService.CreateSession("Test Session").SessionId;

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, profile.ProfileId, "Fetch the page and summarize it.", workspace.WorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public async Task QueueUserMessageAsync_FailsCleanly_WhenProfileIsMissing()
    {
        const string toolId = "fetch_page";
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var workspace = runtime.WorkspaceService.CreateWorkspace("Unprofiled Workspace");
        var session = runtime.SessionService.CreateSession("Unprofiled Session");

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(session.SessionId, "missing-profile", "Hello", workspace.WorkspaceId);

        Assert.Equal(AgentRunStatus.Failed, checkpoint.Status);
        Assert.Contains("agent profile", checkpoint.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueueUserMessageAsync_AllowsToolFromLegacyAliasAssignment()
    {
        const string toolId = "shell";
        const string legacyToolId = "bash";

        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 => AssertToolAvailableAndRequest(request, toolId, "call-1", "{\"command\":\"pwd\"}"),
            2 => AssertAndComplete(request, toolId, "call-1"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}.")
        });

        var tool = new TestTool(toolId, [legacyToolId]);
        using var runtime = AgentTestRuntime.Create(provider, tool);
        var sessionId = await runtime.CreateSessionAsync(legacyToolId);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, "Run the legacy shell tool.", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public async Task QueueUserMessageAsync_KeepsEntireActiveExchangeWhenWorkingSummaryBoundsOlderHistory()
    {
        const string toolId = "fetch_page";
        const string currentUserMessage = "Current request: fetch the current page.";
        const int toolLoopCount = 10;

        var provider = new ScriptedProvider((request, requestIndex) =>
        {
            AssertActiveExchange(request, requestIndex, currentUserMessage);

            if (requestIndex == 1)
            {
                Assert.DoesNotContain(request.Turns, turn => RenderTurnText(turn) == "old-00");
                Assert.Contains(request.Turns, turn => RenderTurnText(turn) == "old-24");
            }

            return requestIndex <= toolLoopCount
                ? ToolRequest($"call-{requestIndex}", toolId, $"{{\"step\":{requestIndex}}}")
                : Complete("All tool results were preserved across the active exchange.");
        });

        using var runtime = AgentTestRuntime.Create(provider, new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);

        for (var index = 0; index < 25; index++)
        {
            var role = index % 2 == 0 ? AgentMessageRole.User : AgentMessageRole.Assistant;
            runtime.SessionService.AppendTextTurn(sessionId, role, $"old-{index:00}");
        }

        runtime.SessionService.SaveWorkingSummary(sessionId, "Existing working summary for earlier turns.");

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, currentUserMessage, runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(toolLoopCount + 1, provider.Requests.Count);
    }

    [Fact]
    public async Task QueueUserMessageAsync_ReusesDuplicateReadOnlyToolCallsWithoutReExecutingTool()
    {
        const string toolId = "fetch_page";

        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 => ToolRequest("call-1", toolId, "{\"url\":\"https://example.com\"}"),
            2 => ToolRequest("call-2", toolId, "{\"url\":\"https://example.com\"}"),
            3 => AssertDuplicateReuseAndComplete(request, toolId),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}.")
        });

        var tool = new TestTool(toolId);
        using var runtime = AgentTestRuntime.Create(provider, tool);
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, "Fetch the page, but do not repeat the same call.", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public async Task QueueUserMessageAsync_AllowsMultipleToolCalls_ForOrchestratedProviderCapability()
    {
        const string firstToolId = "first_tool";
        const string secondToolId = "second_tool";

        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 =>
            [
                ToolRequests(
                    new AgentToolCallRequest("call-1", firstToolId, "{\"value\":1}"),
                    new AgentToolCallRequest("call-2", secondToolId, "{\"value\":2}")),
            ],
            2 =>
            [
                AssertAndComplete(request, firstToolId, "call-1"),
            ],
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}.")
        }, supportsMultipleToolCalls: true);
        var firstTool = new TestTool(firstToolId);
        var secondTool = new TestTool(secondToolId);
        using var runtime = AgentTestRuntime.Create(provider, firstTool, secondTool);
        var sessionId = await runtime.CreateSessionAsync(firstToolId);
        var profile = runtime.CurrentProfile;
        runtime.ProfileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            profile.ChatProviderId,
            profile.ChatModelId,
            profile.EmbeddingProviderId,
            profile.EmbeddingModelId,
            selectableCapabilityAssignments:
            [
                new AgentProfileSelectableCapabilityAssignmentRecord(AgentProfileSelectableCapabilityKinds.Tool, firstToolId),
                new AgentProfileSelectableCapabilityAssignmentRecord(AgentProfileSelectableCapabilityKinds.Tool, secondToolId),
            ],
            behaviorLoopId: "orchestrated");

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, "Use both tools.", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(1, firstTool.ExecutionCount);
        Assert.Equal(1, secondTool.ExecutionCount);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains(provider.Requests[1].Turns, turn =>
            turn.Kind == AgentTurnKind.ToolResult
            && turn.Items.Any(item => item.ToolId == secondToolId && item.CallId == "call-2"));
    }

    [Fact]
    public async Task ApprovePendingPermissionAsync_ContinuesProviderAfterApprovedToolResult()
    {
        const string toolId = "approval_tool";

        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 => ToolRequest("call-1", toolId, "{\"path\":\"~\"}"),
            2 => AssertAndComplete(request, toolId, "call-1"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}.")
        });

        using var runtime = AgentTestRuntime.Create(provider);
        var toolSource = new PermissionedToolSource(toolId);
        runtime.ExtensionCatalog.AddExtension(PackageExtensionPoints.ToolSources, toolSource);
        runtime.ExtensionCatalog.AddExtension(PackageExtensionPoints.PermissionSurfaces, toolSource);
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var waitingCheckpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, "Use the approval tool.", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.WaitingForApproval, waitingCheckpoint.Status);
        var pending = Assert.Single(runtime.PermissionService.ListPendingRequests(sessionId));

        var completedCheckpoint = await runtime.RunCoordinator.ApprovePendingPermissionAsync(sessionId, pending.RequestId);

        Assert.NotNull(completedCheckpoint);
        Assert.Equal(AgentRunStatus.Completed, completedCheckpoint!.Status);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(1, toolSource.ExecutionCount);
        Assert.Contains(runtime.SessionService.ListTurns(sessionId), turn =>
            turn.Role == AgentMessageRole.Assistant
            && turn.Kind == AgentTurnKind.Message
            && RenderTurnText(turn).Contains("Used the tool result", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PermissionEvaluation_InheritsUnrestrictedModeFromParentSession()
    {
        const string toolId = "approval_tool";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
        var toolSource = new PermissionedToolSource(toolId);
        runtime.ExtensionCatalog.AddExtension(PackageExtensionPoints.PermissionSurfaces, toolSource);
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Child Session",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        runtime.PermissionService.SetSessionUnrestrictedMode(parentSessionId, true);

        var evaluation = runtime.PermissionService.Evaluate(
            childSession.SessionId,
            new AgentPermissionRequest(
                PermissionedToolSource.ActionIdForTests,
                PermissionedToolSource.BoundaryIdForTests,
                "Execute approval tool"));

        Assert.Equal(AgentPermissionDecision.Allow, evaluation.Decision);
        Assert.Contains("inherited Unrestricted Mode", evaluation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingPermissionRequestsForSessionTree_IncludesChildRequests()
    {
        const string toolId = "approval_tool";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Child Session",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");

        runtime.PermissionService.SavePendingRequest(new AgentPendingPermissionRequestRecord(
            "request-1",
            childSession.SessionId,
            Guid.NewGuid(),
            1,
            runtime.CurrentProfileId,
            Guid.NewGuid(),
            "parent task",
            "call-1",
            PermissionedToolSource.ActionIdForTests,
            PermissionedToolSource.BoundaryIdForTests,
            "Execute approval tool",
            toolId,
            "{}",
            null,
            null,
            runtime.CurrentWorkspaceId,
            null,
            null,
            null,
            true,
            DateTimeOffset.UtcNow,
            childSession.ParentSessionId,
            childSession.RootSessionId));

        var pending = runtime.PermissionService.ListPendingRequestsForSessionTree(parentSessionId);

        var request = Assert.Single(pending);
        Assert.Equal(childSession.SessionId, request.SessionId);
    }

    [Fact]
    public async Task PermissionEvaluation_InheritsSessionApprovalFromParentSession()
    {
        const string toolId = "approval_tool";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
        var toolSource = new PermissionedToolSource(toolId);
        runtime.ExtensionCatalog.AddExtension(PackageExtensionPoints.PermissionSurfaces, toolSource);
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Child Session",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        runtime.PermissionService.SaveSessionApproval(
            parentSessionId,
            PermissionedToolSource.ActionIdForTests,
            PermissionedToolSource.BoundaryIdForTests);

        var evaluation = runtime.PermissionService.Evaluate(
            childSession.SessionId,
            new AgentPermissionRequest(
                PermissionedToolSource.ActionIdForTests,
                PermissionedToolSource.BoundaryIdForTests,
                "Execute approval tool"));

        Assert.Equal(AgentPermissionDecision.Allow, evaluation.Decision);
        Assert.Contains("session-scoped approval", evaluation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SubagentService_SaveSubagent_RequiresDescription()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-subagent-description-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Researcher");

            Assert.False(subagent.HasRequiredDescription);
            var ex = Assert.Throws<InvalidOperationException>(() => service.SaveSubagent(
                subagent.SubagentId,
                subagent.DisplayName,
                " ",
                subagent.Instructions,
                null,
                null,
                []));
            Assert.Contains("description is required", ex.Message, StringComparison.OrdinalIgnoreCase);
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
    public void SubagentService_SaveSubagent_ClearsModelSettingsWhenProviderInherits()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-subagent-model-inherit-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Researcher");

            var saved = service.SaveSubagent(
                subagent.SubagentId,
                subagent.DisplayName,
                "Investigates delegated research tasks.",
                subagent.Instructions,
                null,
                "stale-model",
                [],
                "{\"reasoningVariantId\":\"high\"}");

            Assert.Null(saved.ChatProviderId);
            Assert.Null(saved.ChatModelId);
            Assert.Null(saved.ChatModelSettingsJson);
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
    public async Task SubagentFeature_ListCapabilitiesAsync_MarksIncompleteSubagentsUnavailable()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-subagent-capability-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var incomplete = service.CreateSubagent("Incomplete");
            var usable = service.CreateSubagent("Researcher");
            service.SaveSubagent(
                usable.SubagentId,
                usable.DisplayName,
                "Investigates delegated research tasks.",
                usable.Instructions,
                null,
                null,
                []);
            var feature = new SubagentFeature(service, new TestExtensionCatalog());

            var capabilities = await feature.ListCapabilitiesAsync(new AgentProfileSelectableCapabilityRequest(Profile: null));

            Assert.Contains(capabilities, capability => capability.CapabilityId == usable.SubagentId && capability.IsSelectable);
            var incompleteCapability = Assert.Single(capabilities, capability => capability.CapabilityId == incomplete.SubagentId);
            Assert.False(incompleteCapability.IsSelectable);
            Assert.Contains("Description is required", incompleteCapability.StatusText, StringComparison.OrdinalIgnoreCase);
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
    public async Task SubagentFeature_ListToolsAsync_RendersDescriptionDrivenGuidance()
    {
        var now = DateTimeOffset.UtcNow;
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-subagent-tool-description-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Researcher");
            service.SaveSubagent(
                subagent.SubagentId,
                subagent.DisplayName,
                "Investigates delegated research tasks and reports concise findings.",
                subagent.Instructions,
                null,
                null,
                []);
            var profile = new AgentProfileRecord(
                "profile-1",
                "Parent",
                null,
                null,
                "provider",
                "model",
                null,
                null,
                now,
                now,
                [],
                [new AgentProfileSelectableCapabilityAssignmentRecord(AgentProfileSelectableCapabilityKinds.Subagent, subagent.SubagentId, SubagentConstants.PackageId)],
                SubagentConstants.OrchestratedBehaviorLoopId);
            var feature = new SubagentFeature(service, new TestExtensionCatalog());

            var tools = await feature.ListToolsAsync(new AgentToolSourceContext(SessionId: null, profile, Workspace: null, ExecutionBinding: null));

            var taskTool = Assert.Single(tools, tool => tool.ToolId == SubagentConstants.TaskToolId);
            Assert.Contains("matches one or more enabled subagent descriptions", taskTool.Description, StringComparison.Ordinal);
            Assert.Contains("Investigates delegated research tasks", taskTool.Description, StringComparison.Ordinal);
            Assert.Contains("Do not invent subagent purposes", taskTool.RuntimeInstructions, StringComparison.Ordinal);
            Assert.DoesNotContain("codebase", taskTool.Description, StringComparison.OrdinalIgnoreCase);
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
    public async Task SubagentTaskTool_ReturnsConfigurationError_WhenSelectedSubagentIsIncomplete()
    {
        var now = DateTimeOffset.UtcNow;
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-subagent-incomplete-task-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Researcher");
            var profile = new AgentProfileRecord(
                "profile-1",
                "Parent",
                null,
                null,
                "provider",
                "model",
                null,
                null,
                now,
                now,
                [],
                [new AgentProfileSelectableCapabilityAssignmentRecord(AgentProfileSelectableCapabilityKinds.Subagent, subagent.SubagentId, SubagentConstants.PackageId)],
                SubagentConstants.OrchestratedBehaviorLoopId);
            var session = new AgentSessionRecord(Guid.NewGuid(), "Parent Session", AgentSessionState.Active, now, now, ProfileId: profile.ProfileId, BehaviorLoopId: profile.BehaviorLoopId);
            var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
            var extensionCatalog = new TestExtensionCatalog();
            extensionCatalog.AddExtension(PackageExtensionPoints.RuntimeCatalogs, new TestRuntimeCatalog([profile], [session], [workspace]));
            extensionCatalog.AddExtension(PackageExtensionPoints.ChildRunExecutors, new CapturingChildRunExecutor());
            var feature = new SubagentFeature(service, extensionCatalog);

            var result = await feature.ExecuteAsync(
                new AgentToolExecutionContext(session.SessionId, profile.ProfileId, workspace, RunId: Guid.NewGuid(), RunRevision: 1, UserTurnId: Guid.NewGuid(), ToolCallId: "task-call"),
                new AgentToolRequest(SubagentConstants.TaskToolId, $"{{\"description\":\"Research\",\"prompt\":\"Do research\",\"subagent_type\":\"{subagent.SubagentId}\"}}"));

            Assert.True(result.IsError);
            Assert.Equal("subagent-description-required", result.ErrorCode);
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
    public async Task QueueUserMessageAsync_ContinuesProviderAfterFailedSubagentResult()
    {
        const string toolId = "failing_subagent";
        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 => ToolRequest("call-1", toolId, "{}"),
            2 => AssertErroredToolResultAndComplete(request, toolId, "call-1", AgentToolResultErrorCodes.SubagentRunFailed, "subagent failed"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}.")
        });
        var tool = new GenericErrorResultTool(toolId, AgentToolResultErrorCodes.SubagentRunFailed, "subagent failed");
        using var runtime = AgentTestRuntime.Create(provider, tool);
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, "Use the failing subagent tool.", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public async Task SubagentTaskTool_UsesSubagentCapabilitySelection()
    {
        const string toolId = "fetch_page";
        var now = DateTimeOffset.UtcNow;
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-subagent-tests", Guid.NewGuid().ToString("N"));
        var store = new SubagentStore(new TestPackageContext(rootPath));
        var service = new SubagentService(store);
        var subagent = service.CreateSubagent("Researcher");
        service.SaveSubagent(
            subagent.SubagentId,
            subagent.DisplayName,
            "Research specialist",
            "Research things.",
            "override-provider",
            "override-model",
            [new AgentProfileSelectableCapabilityAssignmentRecord(AgentProfileSelectableCapabilityKinds.Tool, toolId)]);
        var profile = new AgentProfileRecord(
            "profile-1",
            "Parent",
            null,
            null,
            "provider",
            "model",
            null,
            null,
            now,
            now,
            [new AgentProfileModelBindingRecord("profile-1", AgentModelCapabilityKinds.Chat, "provider", "model", null, now)],
            [new AgentProfileSelectableCapabilityAssignmentRecord(AgentProfileSelectableCapabilityKinds.Subagent, subagent.SubagentId, SubagentConstants.PackageId)],
            SubagentConstants.OrchestratedBehaviorLoopId);
        var session = new AgentSessionRecord(Guid.NewGuid(), "Parent Session", AgentSessionState.Active, now, now, ProfileId: profile.ProfileId, BehaviorLoopId: profile.BehaviorLoopId);
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
        var childExecutor = new CapturingChildRunExecutor();
        var runtimeCatalog = new TestRuntimeCatalog([profile], [session], [workspace]);
        var extensionCatalog = new TestExtensionCatalog();
        extensionCatalog.AddExtension(PackageExtensionPoints.RuntimeCatalogs, runtimeCatalog);
        extensionCatalog.AddExtension(PackageExtensionPoints.ChildRunExecutors, childExecutor);
        var feature = new SubagentFeature(service, extensionCatalog);

        var result = await feature.ExecuteAsync(
            new AgentToolExecutionContext(session.SessionId, profile.ProfileId, workspace, RunId: Guid.NewGuid(), RunRevision: 1, UserTurnId: Guid.NewGuid(), ToolCallId: "task-call"),
            new AgentToolRequest(SubagentConstants.TaskToolId, $"{{\"description\":\"Research\",\"prompt\":\"Do research\",\"subagent_type\":\"{subagent.SubagentId}\"}}"));

        Assert.False(result.IsError);
        Assert.NotNull(childExecutor.Request);
        Assert.Equal("override-provider", childExecutor.Request!.ChildProfile.ChatProviderId);
        Assert.Equal("override-model", childExecutor.Request.ChildProfile.ChatModelId);
        Assert.Contains(childExecutor.Request.ChildProfile.ModelBindings ?? [], binding =>
            binding.CapabilityKind == AgentModelCapabilityKinds.Chat
            && binding.ProviderId == "override-provider"
            && binding.ModelId == "override-model");
        Assert.Contains(childExecutor.Request!.ChildProfile.SelectableCapabilityAssignments ?? [], assignment =>
            assignment.Kind == AgentProfileSelectableCapabilityKinds.Tool && assignment.CapabilityId == toolId);
        Assert.False(string.IsNullOrWhiteSpace(result.StructuredPayloadJson));
        using var payloadDocument = JsonDocument.Parse(result.StructuredPayloadJson!);
        Assert.Equal("Researcher", payloadDocument.RootElement.GetProperty("subagentName").GetString());
        Assert.Equal("Research", payloadDocument.RootElement.GetProperty("childSessionTitle").GetString());
        Assert.Equal(result.BackendId, Guid.Parse(payloadDocument.RootElement.GetProperty("childSessionId").GetString()!).ToString("N"));
    }

    [Fact]
    public async Task SubagentTaskTool_InheritsParentChatModelSettings_WhenNoOverrideIsConfigured()
    {
        var now = DateTimeOffset.UtcNow;
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-subagent-inherit-settings-tests", Guid.NewGuid().ToString("N"));
        var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
        var subagent = service.CreateSubagent("Researcher");
        service.SaveSubagent(
            subagent.SubagentId,
            subagent.DisplayName,
            "Research specialist",
            "Research things.",
            null,
            null,
            []);
        const string parentSettingsJson = "{\"reasoningVariantId\":\"high\"}";
        var profile = new AgentProfileRecord(
            "profile-1",
            "Parent",
            null,
            null,
            "provider",
            "model",
            null,
            null,
            now,
            now,
            [new AgentProfileModelBindingRecord("profile-1", AgentModelCapabilityKinds.Chat, "provider", "model", parentSettingsJson, now)],
            [new AgentProfileSelectableCapabilityAssignmentRecord(AgentProfileSelectableCapabilityKinds.Subagent, subagent.SubagentId, SubagentConstants.PackageId)],
            SubagentConstants.OrchestratedBehaviorLoopId);
        var session = new AgentSessionRecord(Guid.NewGuid(), "Parent Session", AgentSessionState.Active, now, now, ProfileId: profile.ProfileId, BehaviorLoopId: profile.BehaviorLoopId);
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
        var childExecutor = new CapturingChildRunExecutor();
        var extensionCatalog = new TestExtensionCatalog();
        extensionCatalog.AddExtension(PackageExtensionPoints.RuntimeCatalogs, new TestRuntimeCatalog([profile], [session], [workspace]));
        extensionCatalog.AddExtension(PackageExtensionPoints.ChildRunExecutors, childExecutor);
        var feature = new SubagentFeature(service, extensionCatalog);

        var result = await feature.ExecuteAsync(
            new AgentToolExecutionContext(session.SessionId, profile.ProfileId, workspace, RunId: Guid.NewGuid(), RunRevision: 1, UserTurnId: Guid.NewGuid(), ToolCallId: "task-call"),
            new AgentToolRequest(SubagentConstants.TaskToolId, $"{{\"description\":\"Research\",\"prompt\":\"Do research\",\"subagent_type\":\"{subagent.SubagentId}\"}}"));

        Assert.False(result.IsError);
        var chatBinding = Assert.Single(childExecutor.Request!.ChildProfile.ModelBindings!, binding => binding.CapabilityKind == AgentModelCapabilityKinds.Chat);
        Assert.Equal(parentSettingsJson, chatBinding.SettingsJson);
    }

    [Fact]
    public async Task SubagentTaskTool_UsesOverrideChatModelSettings_WhenOverrideIsConfigured()
    {
        var now = DateTimeOffset.UtcNow;
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-subagent-override-settings-tests", Guid.NewGuid().ToString("N"));
        var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
        var subagent = service.CreateSubagent("Researcher");
        const string overrideSettingsJson = "{\"reasoningVariantId\":\"low\"}";
        service.SaveSubagent(
            subagent.SubagentId,
            subagent.DisplayName,
            "Research specialist",
            "Research things.",
            "override-provider",
            "override-model",
            [],
            overrideSettingsJson);
        var profile = new AgentProfileRecord(
            "profile-1",
            "Parent",
            null,
            null,
            "provider",
            "model",
            null,
            null,
            now,
            now,
            [new AgentProfileModelBindingRecord("profile-1", AgentModelCapabilityKinds.Chat, "provider", "model", "{\"reasoningVariantId\":\"high\"}", now)],
            [new AgentProfileSelectableCapabilityAssignmentRecord(AgentProfileSelectableCapabilityKinds.Subagent, subagent.SubagentId, SubagentConstants.PackageId)],
            SubagentConstants.OrchestratedBehaviorLoopId);
        var session = new AgentSessionRecord(Guid.NewGuid(), "Parent Session", AgentSessionState.Active, now, now, ProfileId: profile.ProfileId, BehaviorLoopId: profile.BehaviorLoopId);
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
        var childExecutor = new CapturingChildRunExecutor();
        var extensionCatalog = new TestExtensionCatalog();
        extensionCatalog.AddExtension(PackageExtensionPoints.RuntimeCatalogs, new TestRuntimeCatalog([profile], [session], [workspace]));
        extensionCatalog.AddExtension(PackageExtensionPoints.ChildRunExecutors, childExecutor);
        var feature = new SubagentFeature(service, extensionCatalog);

        var result = await feature.ExecuteAsync(
            new AgentToolExecutionContext(session.SessionId, profile.ProfileId, workspace, RunId: Guid.NewGuid(), RunRevision: 1, UserTurnId: Guid.NewGuid(), ToolCallId: "task-call"),
            new AgentToolRequest(SubagentConstants.TaskToolId, $"{{\"description\":\"Research\",\"prompt\":\"Do research\",\"subagent_type\":\"{subagent.SubagentId}\"}}"));

        Assert.False(result.IsError);
        var chatBinding = Assert.Single(childExecutor.Request!.ChildProfile.ModelBindings!, binding => binding.CapabilityKind == AgentModelCapabilityKinds.Chat);
        Assert.Equal("override-provider", chatBinding.ProviderId);
        Assert.Equal("override-model", chatBinding.ModelId);
        Assert.Equal(overrideSettingsJson, chatBinding.SettingsJson);
    }

    [Fact]
    public async Task SubagentDelegateTasksTool_RunsReadOnlySubagentsConcurrently()
    {
        var now = DateTimeOffset.UtcNow;
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-subagent-batch-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var first = service.CreateSubagent("Researcher");
            service.SaveSubagent(
                first.SubagentId,
                first.DisplayName,
                "Investigates delegated research tasks.",
                first.Instructions,
                null,
                null,
                []);
            var second = service.CreateSubagent("Reviewer");
            service.SaveSubagent(
                second.SubagentId,
                second.DisplayName,
                "Reviews delegated findings for risks.",
                second.Instructions,
                null,
                null,
                []);
            var profile = new AgentProfileRecord(
                "profile-1",
                "Parent",
                null,
                null,
                "provider",
                "model",
                null,
                null,
                now,
                now,
                [],
                [
                    new AgentProfileSelectableCapabilityAssignmentRecord(AgentProfileSelectableCapabilityKinds.Subagent, first.SubagentId, SubagentConstants.PackageId),
                    new AgentProfileSelectableCapabilityAssignmentRecord(AgentProfileSelectableCapabilityKinds.Subagent, second.SubagentId, SubagentConstants.PackageId),
                ],
                SubagentConstants.OrchestratedBehaviorLoopId);
            var session = new AgentSessionRecord(Guid.NewGuid(), "Parent Session", AgentSessionState.Active, now, now, ProfileId: profile.ProfileId, BehaviorLoopId: profile.BehaviorLoopId);
            var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
            var childExecutor = new CapturingChildRunExecutor();
            var extensionCatalog = new TestExtensionCatalog();
            extensionCatalog.AddExtension(PackageExtensionPoints.RuntimeCatalogs, new TestRuntimeCatalog([profile], [session], [workspace]));
            extensionCatalog.AddExtension(PackageExtensionPoints.ChildRunExecutors, childExecutor);
            var feature = new SubagentFeature(service, extensionCatalog);
            var argumentsJson = JsonSerializer.Serialize(new
            {
                tasks = new[]
                {
                    new { description = "Research", prompt = "Research the target.", subagent_type = first.SubagentId },
                    new { description = "Review", prompt = "Review the findings.", subagent_type = second.SubagentId },
                }
            });

            var result = await feature.ExecuteAsync(
                new AgentToolExecutionContext(session.SessionId, profile.ProfileId, workspace, RunId: Guid.NewGuid(), RunRevision: 1, UserTurnId: Guid.NewGuid(), ToolCallId: "batch-call"),
                new AgentToolRequest(SubagentConstants.DelegateTasksToolId, argumentsJson));

            Assert.False(result.IsError);
            Assert.Equal(2, childExecutor.Requests.Count);
            Assert.Contains("<task_result", result.Content, StringComparison.Ordinal);
            Assert.Contains("Researcher", result.Content, StringComparison.Ordinal);
            Assert.Contains("Reviewer", result.Content, StringComparison.Ordinal);
            using var payloadDocument = JsonDocument.Parse(result.StructuredPayloadJson!);
            Assert.Equal(2, payloadDocument.RootElement.GetProperty("tasks").GetArrayLength());
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
    public async Task SubagentDelegateTasksTool_RejectsMutatingSubagents()
    {
        const string toolId = "write_file";
        var now = DateTimeOffset.UtcNow;
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-subagent-batch-mutation-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Builder");
            service.SaveSubagent(
                subagent.SubagentId,
                subagent.DisplayName,
                "Implements delegated changes.",
                subagent.Instructions,
                null,
                null,
                [new AgentProfileSelectableCapabilityAssignmentRecord(AgentProfileSelectableCapabilityKinds.Tool, toolId)]);
            var profile = new AgentProfileRecord(
                "profile-1",
                "Parent",
                null,
                null,
                "provider",
                "model",
                null,
                null,
                now,
                now,
                [],
                [new AgentProfileSelectableCapabilityAssignmentRecord(AgentProfileSelectableCapabilityKinds.Subagent, subagent.SubagentId, SubagentConstants.PackageId)],
                SubagentConstants.OrchestratedBehaviorLoopId);
            var session = new AgentSessionRecord(Guid.NewGuid(), "Parent Session", AgentSessionState.Active, now, now, ProfileId: profile.ProfileId, BehaviorLoopId: profile.BehaviorLoopId);
            var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
            var childExecutor = new CapturingChildRunExecutor();
            var extensionCatalog = new TestExtensionCatalog();
            extensionCatalog.AddExtension(PackageExtensionPoints.RuntimeCatalogs, new TestRuntimeCatalog([profile], [session], [workspace]));
            extensionCatalog.AddExtension(PackageExtensionPoints.ChildRunExecutors, childExecutor);
            extensionCatalog.AddExtension(PackageExtensionPoints.Tools, new TestMutableTool(toolId));
            var feature = new SubagentFeature(service, extensionCatalog);
            var argumentsJson = JsonSerializer.Serialize(new
            {
                tasks = new[]
                {
                    new { description = "Build", prompt = "Implement the change.", subagent_type = subagent.SubagentId },
                }
            });

            var result = await feature.ExecuteAsync(
                new AgentToolExecutionContext(session.SessionId, profile.ProfileId, workspace, RunId: Guid.NewGuid(), RunRevision: 1, UserTurnId: Guid.NewGuid(), ToolCallId: "batch-call"),
                new AgentToolRequest(SubagentConstants.DelegateTasksToolId, argumentsJson));

            Assert.True(result.IsError);
            Assert.Equal("subagent-batch-not-read-only", result.ErrorCode);
            Assert.Empty(childExecutor.Requests);
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
    public void SubagentTaskPresentation_ResolvesSubagentIdToDisplayName()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-subagent-presentation-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var store = new SubagentStore(new TestPackageContext(rootPath));
            var service = new SubagentService(store);
            var subagent = service.CreateSubagent("Researcher");
            var feature = new SubagentFeature(service, new TestExtensionCatalog());
            var argumentsJson = JsonSerializer.Serialize(new
            {
                description = "Research",
                prompt = "Do research",
                subagent_type = subagent.SubagentId,
            });

            var presentation = feature.ResolveToolPresentation(new AgentToolPresentationRequest(
                SubagentConstants.TaskToolId,
                argumentsJson,
                ResultSummary: null,
                TextContent: null,
                StructuredPayloadJson: null,
                SourcesJson: null,
                IsError: false,
                ErrorCode: null,
                BackendId: null));

            Assert.NotNull(presentation);
            Assert.Equal("Researcher subagent · Research", presentation!.HeaderText);
            Assert.Contains("Subagent: Researcher subagent", presentation.DetailMarkdown, StringComparison.Ordinal);
            Assert.DoesNotContain(subagent.SubagentId, presentation.HeaderText, StringComparison.OrdinalIgnoreCase);
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
    public async Task RunChildAsync_ReusesChildSession_WhenTaskIdMatches()
    {
        var provider = new ScriptedProvider((_, requestIndex) => Complete($"done-{requestIndex}"));
        using var runtime = AgentTestRuntime.Create(provider);
        var parentSessionId = await runtime.CreateSessionAsync("noop");
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childProfile = runtime.CurrentProfile with
        {
            ProfileId = "child-profile",
            DisplayName = "Child Agent",
            ModelBindings = [new AgentProfileModelBindingRecord("child-profile", AgentModelCapabilityKinds.Chat, "test-provider", "test-model", null, DateTimeOffset.UtcNow)],
            SelectableCapabilityAssignments = [],
            IsInternal = true,
        };

        var first = await runtime.RunCoordinator.RunChildAsync(new AgentChildRunRequest(
            parentSessionId,
            Guid.NewGuid(),
            1,
            "task-call-1",
            runtime.CurrentWorkspaceId,
            "task-123",
            childProfile,
            "First task prompt.",
            "Task 123"));
        var second = await runtime.RunCoordinator.RunChildAsync(new AgentChildRunRequest(
            parentSessionId,
            Guid.NewGuid(),
            2,
            "task-call-2",
            runtime.CurrentWorkspaceId,
            "task-123",
            childProfile,
            "Second task prompt.",
            "Task 123"));

        Assert.Equal(AgentRunStatus.Completed, first.Status);
        Assert.Equal(AgentRunStatus.Completed, second.Status);
        Assert.Equal(first.SessionId, second.SessionId);
        var childSession = Assert.Single(
            runtime.SessionService.ListSessions(),
            session => session.ParentSessionId == parentSessionId && string.Equals(session.TaskId, "task-123", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(first.SessionId, childSession.SessionId);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task StopAsync_StopsParentSessionTree()
    {
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
        var parentSessionId = await runtime.CreateSessionAsync("noop");
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Child Session",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        var grandchildSession = runtime.SessionService.CreateSession(
            "Grandchild Session",
            parentSessionId: childSession.SessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        var completedChildSession = runtime.SessionService.CreateSession(
            "Completed Child Session",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        runtime.SessionService.SaveCheckpoint(parentSessionId, 1, AgentRunStatus.Running, "Parent running.");
        runtime.SessionService.SaveCheckpoint(childSession.SessionId, 1, AgentRunStatus.Running, "Child running.");
        runtime.SessionService.SaveCheckpoint(grandchildSession.SessionId, 1, AgentRunStatus.WaitingForApproval, "Grandchild waiting.");
        runtime.SessionService.SaveCheckpoint(completedChildSession.SessionId, 1, AgentRunStatus.Completed, "Completed child.");

        var checkpoint = await runtime.RunCoordinator.StopAsync(parentSessionId);

        Assert.NotNull(checkpoint);
        Assert.Equal(AgentRunStatus.Stopped, checkpoint!.Status);
        Assert.Equal(AgentRunStatus.Stopped, runtime.SessionService.GetLatestCheckpoint(childSession.SessionId)!.Status);
        Assert.Equal(AgentRunStatus.Stopped, runtime.SessionService.GetLatestCheckpoint(grandchildSession.SessionId)!.Status);
        Assert.Equal(AgentRunStatus.Completed, runtime.SessionService.GetLatestCheckpoint(completedChildSession.SessionId)!.Status);
        Assert.Equal(AgentSessionState.Stopped, runtime.SessionService.GetSession(childSession.SessionId)!.State);
        Assert.Equal(AgentSessionState.Stopped, runtime.SessionService.GetSession(grandchildSession.SessionId)!.State);
    }

    [Fact]
    public async Task StopAsync_ClearsPendingPermissionRequestsForStoppedSubsessions()
    {
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
        var parentSessionId = await runtime.CreateSessionAsync("approval_tool");
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Child Session",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        runtime.SessionService.SaveCheckpoint(parentSessionId, 1, AgentRunStatus.Running, "Parent running.");
        runtime.SessionService.SaveCheckpoint(childSession.SessionId, 1, AgentRunStatus.WaitingForApproval, "Child waiting.");
        runtime.PermissionService.SavePendingRequest(new AgentPendingPermissionRequestRecord(
            "request-1",
            childSession.SessionId,
            Guid.NewGuid(),
            1,
            runtime.CurrentProfileId,
            Guid.NewGuid(),
            "Use the approval tool.",
            "call-1",
            "approval_action",
            "approval_boundary",
            "Approve child tool use.",
            "approval_tool",
            "{}",
            null,
            null,
            runtime.CurrentWorkspaceId,
            null,
            null,
            null,
            true,
            DateTimeOffset.UtcNow,
            childSession.ParentSessionId,
            childSession.RootSessionId));

        await runtime.RunCoordinator.StopAsync(parentSessionId);

        Assert.Empty(runtime.PermissionService.ListPendingRequests(childSession.SessionId));
        Assert.Equal(AgentRunStatus.Stopped, runtime.SessionService.GetLatestCheckpoint(childSession.SessionId)!.Status);
    }

    [Fact]
    public async Task DenyPendingPermission_AppendsErrorToolResultAndAllowsNextMessage()
    {
        const string toolId = "approval_tool";

        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 => ToolRequest("call-1", toolId, "{\"path\":\"~\"}"),
            2 => AssertDeniedToolResultAndComplete(request, toolId, "call-1"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}.")
        });

        using var runtime = AgentTestRuntime.Create(provider);
        var toolSource = new PermissionedToolSource(toolId);
        runtime.ExtensionCatalog.AddExtension(PackageExtensionPoints.ToolSources, toolSource);
        runtime.ExtensionCatalog.AddExtension(PackageExtensionPoints.PermissionSurfaces, toolSource);
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var waitingCheckpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, "Use the approval tool.", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.WaitingForApproval, waitingCheckpoint.Status);
        var pending = Assert.Single(runtime.PermissionService.ListPendingRequests(sessionId));

        var deniedCheckpoint = runtime.RunCoordinator.DenyPendingPermission(sessionId, pending.RequestId);

        Assert.NotNull(deniedCheckpoint);
        Assert.Equal(AgentRunStatus.Stopped, deniedCheckpoint!.Status);
        Assert.Empty(runtime.PermissionService.ListPendingRequests(sessionId));
        Assert.Equal(0, toolSource.ExecutionCount);
        Assert.Contains(runtime.SessionService.ListTurns(sessionId), turn =>
            turn.Kind == AgentTurnKind.ToolResult
            && turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolResult
                                      && item.CallId == "call-1"
                                      && item.ToolId == toolId
                                      && item.IsError
                                      && item.ErrorCode == "permission-denied"));

        var completedCheckpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, "Now write a calm poem.", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, completedCheckpoint.Status);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(0, toolSource.ExecutionCount);
    }

    [Fact]
    public async Task QueueUserMessageAsync_DropsHistoricalToolCallWithoutResult()
    {
        const string toolId = "fetch_page";
        const string orphanCallId = "orphan-call";

        var provider = new ScriptedProvider((request, requestIndex) =>
        {
            Assert.Equal(1, requestIndex);
            Assert.DoesNotContain(request.Turns, turn =>
                turn.Kind == AgentTurnKind.ToolCall
                && turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolCall
                                          && item.CallId == orphanCallId));
            AssertNoUnpairedToolItems(request);
            return Complete("done");
        });

        using var runtime = AgentTestRuntime.Create(provider, new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.User, "older request");
        runtime.SessionService.AppendToolCallTurn(sessionId, AgentMessageRole.Assistant, orphanCallId, toolId, "{\"url\":\"https://example.com\"}");

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, "Continue without the old tool call.", runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
    }

    [Theory]
    [InlineData(5000, 5000)]
    [InlineData(null, 300000)]
    public void ResolveEffectiveTimeoutMilliseconds_UsesServerTimeoutThenDefaultFallback(
        int? serverTimeoutMilliseconds,
        int? expectedTimeoutMilliseconds)
    {
        var resolvedTimeout = McpTimeoutResolver.ResolveEffectiveTimeoutMilliseconds(serverTimeoutMilliseconds);

        Assert.Equal(expectedTimeoutMilliseconds, resolvedTimeout);
    }

    [Fact]
    public void AgentChatViewModel_ShowsSetupState_WhenNoProfileExists()
    {
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);

        Assert.Empty(viewModel.Profiles);
        Assert.Null(viewModel.SelectedWorkspace);
        Assert.Null(viewModel.SelectedProfile);
        Assert.Null(viewModel.SelectedSession);
        Assert.True(viewModel.HasNoProfiles);
        Assert.False(viewModel.CanUseChat);
        Assert.True(viewModel.CannotUseChat);
        Assert.True(viewModel.ShowSetupInstructions);
        Assert.True(viewModel.ShowTranscriptSurface);
        Assert.False(viewModel.ShowCollapsedComposer);
        Assert.False(viewModel.ShowExpandedComposer);
        Assert.False(viewModel.IsSelectedSessionRunInactive);
        Assert.Equal("Create an agent profile before chatting", viewModel.SetupTitle);
    }

    [Fact]
    public async Task AgentChatViewModel_PreselectsFirstWorkspaceAndSession_WhenAvailable()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);

        Assert.Equal(runtime.CurrentWorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal(runtime.CurrentProfileId, viewModel.SelectedProfile?.ProfileId);
        Assert.Equal(sessionId, viewModel.SelectedSession?.SessionId);
        Assert.True(viewModel.CanUseChat);
        Assert.False(viewModel.ShowSetupInstructions);
        Assert.True(viewModel.ShowCollapsedComposer);
    }

    [Fact]
    public async Task AgentChatViewModel_PreservesSelectedSession_WhenWorkspaceChanges()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var secondWorkspace = runtime.WorkspaceService.CreateWorkspace("Second Workspace");
        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);

        viewModel.SelectedWorkspace = viewModel.Workspaces.Single(workspace => workspace.WorkspaceId == secondWorkspace.WorkspaceId);

        Assert.Equal(secondWorkspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal(sessionId, viewModel.SelectedSession?.SessionId);
        Assert.Contains(viewModel.Sessions, session => session.SessionId == sessionId);
        Assert.True(viewModel.CanUseChat);
    }

    [Fact]
    public async Task AgentChatViewModel_SelectsFallbackSession_WhenSelectedSessionIsDeleted()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var deletedSessionId = await runtime.CreateSessionAsync(toolId);
        var fallbackSession = runtime.SessionService.CreateSession("Fallback Session");
        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);
        viewModel.SelectedSession = viewModel.Sessions.Single(session => session.SessionId == deletedSessionId);

        runtime.SessionService.DeleteSession(deletedSessionId);

        Assert.Equal(fallbackSession.SessionId, viewModel.SelectedSession?.SessionId);
        Assert.Equal(fallbackSession.SessionId, viewModel.DisplayedSession?.SessionId);
        Assert.DoesNotContain(viewModel.Sessions, session => session.SessionId == deletedSessionId);
    }

    [Fact]
    public async Task QueueUserMessageAsync_UsesProvidedWorkspaceContext()
    {
        const string toolId = "fetch_page";
        var provider = new ScriptedProvider((_, requestIndex) => requestIndex switch
        {
            1 => ToolRequest("call-1", toolId, "{}"),
            2 => Complete("done"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}.")
        });
        var tool = new TestTool(toolId);

        using var runtime = AgentTestRuntime.Create(provider, tool);
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var secondWorkspace = runtime.WorkspaceService.CreateWorkspace("Second Workspace");

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(sessionId, runtime.CurrentProfileId, "Use the selected workspace.", secondWorkspace.WorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(secondWorkspace.WorkspaceId, tool.LastWorkspaceId);
    }

    [Fact]
    public void AgentSessionsViewModel_CreatesRenamesAndDeletesSession()
    {
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
        using var viewModel = new AgentSessionsViewModel(runtime.SessionService);

        viewModel.CreateSessionCommand.Execute(null);
        var sessionId = Assert.Single(viewModel.Sessions).SessionId;

        viewModel.Title = "Renamed Session";
        viewModel.SaveSessionCommand.Execute(null);

        Assert.Equal("Renamed Session", runtime.SessionService.GetSession(sessionId)?.Title);

        viewModel.DeleteSessionCommand.Execute(null);

        Assert.Empty(viewModel.Sessions);
        Assert.Null(runtime.SessionService.GetSession(sessionId));
    }

    [Fact]
    public async Task AgentProfilesViewModel_CompactLayout_UsesRowActivationAndReturnsToList()
    {
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
        var profile = await runtime.ProfileService.CreateProfileAsync("Alpha Profile");
        using var viewModel = new AgentProfilesViewModel(runtime.ProfileService);
        await WaitUntilAsync(() => viewModel.SelectedProfile?.ProfileId == profile.ProfileId);

        viewModel.IsCompactLayout = true;

        Assert.Null(viewModel.SelectedProfile);
        Assert.False(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowCompactList);

        viewModel.ActivateProfile(viewModel.Profiles.Single(item => item.ProfileId == profile.ProfileId));

        Assert.True(viewModel.IsEditorActive);
        Assert.Equal(profile.ProfileId, viewModel.SelectedProfile?.ProfileId);
        Assert.True(viewModel.ShowCompactEditor);

        viewModel.BackToProfileListCommand.Execute(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Null(viewModel.SelectedProfile);
        Assert.True(viewModel.ShowCompactList);

        viewModel.IsCompactLayout = false;

        Assert.Equal(profile.ProfileId, viewModel.SelectedProfile?.ProfileId);
        Assert.True(viewModel.ShowListPane);
        Assert.True(viewModel.ShowEditorPane);
    }

    [Fact]
    public async Task AgentProfilesViewModel_SaveAndDelete_CompactLayout_ReturnToListAndClearSelection()
    {
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
        var profile = await runtime.ProfileService.CreateProfileAsync("Alpha Profile");
        using var viewModel = new AgentProfilesViewModel(runtime.ProfileService);
        await WaitUntilAsync(() => viewModel.SelectedProfile?.ProfileId == profile.ProfileId);
        viewModel.IsCompactLayout = true;
        viewModel.ActivateProfile(viewModel.Profiles.Single(item => item.ProfileId == profile.ProfileId));
        await WaitUntilAsync(() => viewModel.DisplayName == "Alpha Profile" && !viewModel.IsBusy);
        viewModel.DisplayName = "Saved Profile";

        await viewModel.SaveProfileCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Null(viewModel.SelectedProfile);
        Assert.Empty(viewModel.StatusText);
        Assert.False(viewModel.HasStatusText);
        Assert.Equal("Saved Profile", runtime.ProfileService.GetProfile(profile.ProfileId)?.DisplayName);

        viewModel.ActivateProfile(viewModel.Profiles.Single(item => item.ProfileId == profile.ProfileId));
        await WaitUntilAsync(() => viewModel.SelectedProfile?.ProfileId == profile.ProfileId && !viewModel.IsBusy);

        await viewModel.DeleteProfileCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Null(viewModel.SelectedProfile);
        Assert.Empty(viewModel.Profiles);
        Assert.Null(runtime.ProfileService.GetProfile(profile.ProfileId));
    }

    [Fact]
    public async Task AgentProfilesViewModel_Save_WideLayout_KeepsEditorLoadedAndAutoClearsSuccess()
    {
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
        var profile = await runtime.ProfileService.CreateProfileAsync("Alpha Profile");
        using var viewModel = new AgentProfilesViewModel(runtime.ProfileService);
        await WaitUntilAsync(() => viewModel.SelectedProfile?.ProfileId == profile.ProfileId);
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
        Assert.Equal("Saved Profile", runtime.ProfileService.GetProfile(profile.ProfileId)?.DisplayName);
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
    public void AgentSessionsViewModel_CompactLayout_UsesRowActivationAndReturnsToList()
    {
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
        var session = runtime.SessionService.CreateSession("Alpha Session");
        using var viewModel = new AgentSessionsViewModel(runtime.SessionService);
        Assert.Equal(session.SessionId, viewModel.SelectedSession?.SessionId);

        viewModel.IsCompactLayout = true;

        Assert.Null(viewModel.SelectedSession);
        Assert.False(viewModel.IsEditorActive);
        Assert.True(viewModel.ShowCompactList);

        viewModel.ActivateSession(viewModel.Sessions.Single(item => item.SessionId == session.SessionId));

        Assert.True(viewModel.IsEditorActive);
        Assert.Equal(session.SessionId, viewModel.SelectedSession?.SessionId);
        Assert.True(viewModel.ShowCompactEditor);

        viewModel.BackToSessionListCommand.Execute(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Null(viewModel.SelectedSession);
        Assert.True(viewModel.ShowCompactList);

        viewModel.IsCompactLayout = false;

        Assert.Equal(session.SessionId, viewModel.SelectedSession?.SessionId);
        Assert.True(viewModel.ShowListPane);
        Assert.True(viewModel.ShowEditorPane);
    }

    [Fact]
    public void AgentSessionsViewModel_SaveAndDelete_CompactLayout_ReturnToListAndClearSelection()
    {
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
        var session = runtime.SessionService.CreateSession("Alpha Session");
        using var viewModel = new AgentSessionsViewModel(runtime.SessionService)
        {
            IsCompactLayout = true,
        };
        viewModel.ActivateSession(viewModel.Sessions.Single(item => item.SessionId == session.SessionId));
        viewModel.Title = "Saved Session";

        viewModel.SaveSessionCommand.Execute(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Null(viewModel.SelectedSession);
        Assert.Empty(viewModel.StatusText);
        Assert.False(viewModel.HasStatusText);
        Assert.Equal("Saved Session", runtime.SessionService.GetSession(session.SessionId)?.Title);

        viewModel.ActivateSession(viewModel.Sessions.Single(item => item.SessionId == session.SessionId));

        viewModel.DeleteSessionCommand.Execute(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Null(viewModel.SelectedSession);
        Assert.Empty(viewModel.Sessions);
        Assert.Null(runtime.SessionService.GetSession(session.SessionId));
    }

    [Fact]
    public async Task AgentSessionsViewModel_Save_WideLayout_KeepsEditorLoadedAndAutoClearsSuccess()
    {
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
        var session = runtime.SessionService.CreateSession("Alpha Session");
        using var viewModel = new AgentSessionsViewModel(runtime.SessionService);
        Assert.Equal(session.SessionId, viewModel.SelectedSession?.SessionId);
        viewModel.Title = "Saved Session";

        viewModel.SaveSessionCommand.Execute(null);

        Assert.False(viewModel.IsEditorActive);
        Assert.Equal(session.SessionId, viewModel.SelectedSession?.SessionId);
        Assert.Equal("Saved Session", viewModel.Title);
        Assert.Equal("Saved Session", runtime.SessionService.GetSession(session.SessionId)?.Title);
        Assert.Equal("Session saved.", viewModel.StatusText);
        Assert.True(viewModel.HasStatusText);
        Assert.True(viewModel.IsStatusSuccess);
        Assert.False(viewModel.IsStatusWarning);
        Assert.False(viewModel.IsStatusError);

        await WaitUntilAsync(() => !viewModel.HasStatusText, TimeSpan.FromSeconds(4));

        Assert.Empty(viewModel.StatusText);
        Assert.Equal(AgentSessionStatusKind.None, viewModel.StatusKind);
    }

    [Fact]
    public async Task SubagentsViewModel_CompactLayout_UsesRowActivationAndReturnsToList()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-subagent-list-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Alpha Subagent");
            using var viewModel = new SubagentsViewModel(service, runtime.ExtensionCatalog)
            {
                IsCompactLayout = true,
            };
            await viewModel.InitializeAsync();

            Assert.Null(viewModel.SelectedSubagent);
            Assert.False(viewModel.IsEditorActive);
            Assert.True(viewModel.ShowCompactList);

            viewModel.ActivateSubagent(viewModel.Subagents.Single(item => item.SubagentId == subagent.SubagentId));

            Assert.True(viewModel.IsEditorActive);
            Assert.Equal(subagent.SubagentId, viewModel.SelectedSubagent?.SubagentId);
            Assert.True(viewModel.ShowCompactEditor);

            viewModel.BackToSubagentListCommand.Execute(null);

            Assert.False(viewModel.IsEditorActive);
            Assert.Null(viewModel.SelectedSubagent);
            Assert.True(viewModel.ShowCompactList);

            viewModel.IsCompactLayout = false;

            Assert.Equal(subagent.SubagentId, viewModel.SelectedSubagent?.SubagentId);
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
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-subagent-list-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Alpha Subagent");
            using var viewModel = new SubagentsViewModel(service, runtime.ExtensionCatalog)
            {
                IsCompactLayout = true,
            };
            await viewModel.InitializeAsync();
            viewModel.ActivateSubagent(viewModel.Subagents.Single(item => item.SubagentId == subagent.SubagentId));
            viewModel.Description = "Handles focused tasks.";

            await viewModel.SaveSubagentCommand.ExecuteAsync(null);

            Assert.False(viewModel.IsEditorActive);
            Assert.Null(viewModel.SelectedSubagent);
            Assert.Empty(viewModel.StatusText);
            Assert.False(viewModel.HasStatusText);
            Assert.Equal("Handles focused tasks.", service.GetSubagent(subagent.SubagentId)?.Description);

            viewModel.ActivateSubagent(viewModel.Subagents.Single(item => item.SubagentId == subagent.SubagentId));

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
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-subagent-list-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Alpha Subagent");
            using var viewModel = new SubagentsViewModel(service, runtime.ExtensionCatalog);
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
            Assert.Equal("Handles focused tasks.", service.GetSubagent(subagent.SubagentId)?.Description);
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
    public async Task SubsessionsViewModel_CompactLayout_UsesRowActivationAndReturnsToList()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        using var viewModel = new SubsessionsViewModel(runtime.ExtensionCatalog)
        {
            IsCompactLayout = true,
        };
        await viewModel.InitializeAsync();

        Assert.Null(viewModel.SelectedSubsession);
        Assert.False(viewModel.IsDetailActive);
        Assert.True(viewModel.ShowCompactList);

        viewModel.ActivateSubsession(viewModel.Subsessions.Single(item => item.SessionId == childSession.SessionId));

        Assert.True(viewModel.IsDetailActive);
        Assert.Equal(childSession.SessionId, viewModel.SelectedSubsession?.SessionId);
        Assert.True(viewModel.ShowCompactDetail);

        viewModel.BackToSubsessionsListCommand.Execute(null);

        Assert.False(viewModel.IsDetailActive);
        Assert.Null(viewModel.SelectedSubsession);
        Assert.True(viewModel.ShowCompactList);

        viewModel.IsCompactLayout = false;

        Assert.Equal(childSession.SessionId, viewModel.SelectedSubsession?.SessionId);
        Assert.True(viewModel.ShowListPane);
        Assert.True(viewModel.ShowDetailPane);
    }

    [Fact]
    public async Task AgentSessionService_DeleteSession_RemovesAttachmentFilesForSessionTree()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var childSession = runtime.SessionService.CreateSession("Child Session", parentSessionId: parentSessionId, rootSessionId: parentSessionId);
        var parentAttachment = await runtime.AttachmentService.StoreAttachmentAsync(
            parentSessionId,
            new AgentAttachmentUploadRequest("parent.txt", "text/plain", Encoding.UTF8.GetBytes("parent attachment")));
        var childAttachment = await runtime.AttachmentService.StoreAttachmentAsync(
            childSession.SessionId,
            new AgentAttachmentUploadRequest("child.txt", "text/plain", Encoding.UTF8.GetBytes("child attachment")));

        Assert.NotEmpty(await runtime.AttachmentService.ReadAttachmentBytesAsync(parentAttachment.Metadata));
        Assert.NotEmpty(await runtime.AttachmentService.ReadAttachmentBytesAsync(childAttachment.Metadata));

        runtime.SessionService.DeleteSession(parentSessionId);

        await Assert.ThrowsAsync<FileNotFoundException>(() => runtime.AttachmentService.ReadAttachmentBytesAsync(parentAttachment.Metadata));
        await Assert.ThrowsAsync<FileNotFoundException>(() => runtime.AttachmentService.ReadAttachmentBytesAsync(childAttachment.Metadata));
    }

    [Fact]
    public async Task AgentSessionService_DeleteSession_RemovesSemanticMemoryForSessionTree()
    {
        const string toolId = "fetch_page";
        const string providerId = "test-embeddings";
        const string modelId = "semantic-v1";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var childSession = runtime.SessionService.CreateSession("Child Session", parentSessionId: parentSessionId, rootSessionId: parentSessionId);
        var otherSession = runtime.SessionService.CreateSession("Other Session");
        var context = new TestPackageContext(Path.Combine(Path.GetTempPath(), "sunder-memory-tests", Guid.NewGuid().ToString("N")));
        var store = new MemoryLocalStore(context);
        var feature = CreateSemanticFeature(store, runtime.ExtensionCatalog);
        runtime.ExtensionCatalog.AddExtension(PackageExtensionPoints.SessionDataCleaners, feature);

        var parentMemory = StoreMemoryWithEmbedding(store, parentSessionId, providerId, modelId, "Parent memory");
        var childMemory = StoreMemoryWithEmbedding(store, childSession.SessionId, providerId, modelId, "Child memory");
        var otherMemory = StoreMemoryWithEmbedding(store, otherSession.SessionId, providerId, modelId, "Other memory");

        runtime.SessionService.DeleteSession(parentSessionId);

        Assert.Null(store.GetMemory(parentMemory.MemoryId));
        Assert.Null(store.GetMemory(childMemory.MemoryId));
        Assert.Empty(store.ListEvidence(parentMemory.MemoryId));
        Assert.Empty(store.ListEvidence(childMemory.MemoryId));
        Assert.Empty(store.ListEmbeddings(parentSessionId, providerId, modelId));
        Assert.Empty(store.ListEmbeddings(childSession.SessionId, providerId, modelId));
        Assert.Empty(store.ListMemories(parentSessionId, searchText: "memory", includeInactive: true));
        Assert.Empty(store.ListMemories(childSession.SessionId, searchText: "memory", includeInactive: true));
        Assert.NotNull(store.GetMemory(otherMemory.MemoryId));
        Assert.NotEmpty(store.ListEmbeddings(otherSession.SessionId, providerId, modelId));
    }

    [Fact]
    public async Task AgentProfilesViewModel_RefreshesSubagentCapabilities_WhenSubagentChanges()
    {
        const string toolId = "fetch_page";
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-subagent-profile-refresh-tests", Guid.NewGuid().ToString("N"));

        try
        {
            using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
            await runtime.CreateSessionAsync(toolId);
            var subagentService = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var incomplete = subagentService.CreateSubagent("Draft Specialist");
            var usable = subagentService.CreateSubagent("Researcher");
            subagentService.SaveSubagent(
                usable.SubagentId,
                usable.DisplayName,
                "Investigates delegated research tasks.",
                usable.Instructions,
                null,
                null,
                []);
            runtime.ExtensionCatalog.AddExtension(PackageExtensionPoints.ProfileSelectableCapabilityProviders, new SubagentFeature(subagentService, runtime.ExtensionCatalog));
            using var viewModel = new AgentProfilesViewModel(runtime.ProfileService);

            await WaitUntilAsync(() => viewModel.PackageCapabilities.Any(capability => capability.CapabilityId == incomplete.SubagentId)
                                    && viewModel.PackageCapabilities.Any(capability => capability.CapabilityId == usable.SubagentId));
            var incompleteOption = viewModel.PackageCapabilities.Single(capability => capability.CapabilityId == incomplete.SubagentId);
            var usableOption = viewModel.PackageCapabilities.Single(capability => capability.CapabilityId == usable.SubagentId);
            Assert.False(incompleteOption.CanSelect);
            Assert.False(incompleteOption.IsEnabled);
            Assert.Contains("Description is required", incompleteOption.StatusText, StringComparison.OrdinalIgnoreCase);
            usableOption.IsEnabled = true;

            subagentService.SaveSubagent(
                incomplete.SubagentId,
                incomplete.DisplayName,
                "Handles focused delegated tasks.",
                incomplete.Instructions,
                null,
                null,
                []);

            await WaitUntilAsync(() => viewModel.PackageCapabilities.Single(capability => capability.CapabilityId == incomplete.SubagentId).CanSelect);
            incompleteOption = viewModel.PackageCapabilities.Single(capability => capability.CapabilityId == incomplete.SubagentId);
            usableOption = viewModel.PackageCapabilities.Single(capability => capability.CapabilityId == usable.SubagentId);
            Assert.True(incompleteOption.CanSelect);
            Assert.True(usableOption.IsEnabled);

            subagentService.SaveSubagent(
                usable.SubagentId,
                "Lead Researcher",
                "Investigates delegated research tasks.",
                usable.Instructions,
                null,
                null,
                []);

            await WaitUntilAsync(() => viewModel.PackageCapabilities.Any(capability => capability.CapabilityId == usable.SubagentId
                                                                                      && capability.DisplayName == "Lead Researcher"));
            usableOption = viewModel.PackageCapabilities.Single(capability => capability.CapabilityId == usable.SubagentId);
            Assert.True(usableOption.IsEnabled);
            Assert.Contains(viewModel.CapabilityGroups, group => group.Title == "Subagents");

            subagentService.DeleteSubagent(incomplete.SubagentId);

            await WaitUntilAsync(() => viewModel.PackageCapabilities.All(capability => capability.CapabilityId != incomplete.SubagentId));
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
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-mcp-profile-refresh-tests", Guid.NewGuid().ToString("N"));

        try
        {
            using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
            await runtime.CreateSessionAsync(toolId);
            var serverCatalogService = new McpServerCatalogService(new TestPackageContext(rootPath));
            await using var connectionManager = new McpClientConnectionManager(NullLoggerFactory.Instance);
            runtime.ExtensionCatalog.AddExtension(
                PackageExtensionPoints.ProfileSelectableCapabilityProviders,
                new McpToolSource(serverCatalogService, connectionManager));
            using var viewModel = new AgentProfilesViewModel(runtime.ProfileService);

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

            await serverCatalogService.SaveServerAsync(server, new Dictionary<string, string>(), new Dictionary<string, string>());

            await WaitUntilAsync(() => viewModel.PackageCapabilities.Any(capability => capability.CapabilityId == server.ServerId));
            var group = Assert.Single(viewModel.PackageCapabilityGroups);
            Assert.Equal("MCP Servers", group.Title);
            Assert.Contains(viewModel.CapabilityGroups, capabilityGroup => capabilityGroup.Title == "MCP Servers");

            var renamedServer = server with
            {
                DisplayName = "Renamed MCP",
                Description = "Renamed MCP tools.",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await serverCatalogService.SaveServerAsync(renamedServer, new Dictionary<string, string>(), new Dictionary<string, string>());

            await WaitUntilAsync(() => viewModel.PackageCapabilities.Any(capability => capability.CapabilityId == server.ServerId
                                                                                      && capability.DisplayName == "Renamed MCP"));

            await serverCatalogService.DeleteServerAsync(server.ServerId);

            await WaitUntilAsync(() => viewModel.PackageCapabilities.All(capability => capability.CapabilityId != server.ServerId));
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
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
        await runtime.CreateSessionAsync("dynamic_tool");
        using var viewModel = new AgentProfilesViewModel(runtime.ProfileService);

        await WaitUntilAsync(() => viewModel.SelectedProfile is not null);
        Assert.Empty(viewModel.LocalTools);

        runtime.ExtensionCatalog.AddExtension(PackageExtensionPoints.Tools, new MetadataTool("dynamic_tool", "Dynamic Tools"));

        await WaitUntilAsync(() => viewModel.LocalTools.Any(tool => tool.CapabilityId == "dynamic_tool"));
        var group = Assert.Single(viewModel.LocalToolGroups);
        Assert.Equal("Dynamic Tools", group.Title);
        Assert.Contains(viewModel.CapabilityGroups, capabilityGroup => capabilityGroup.Title == "Dynamic Tools");
    }

    [Fact]
    public async Task AgentProfilesViewModel_RefreshesCapabilities_WhenProviderIsAddedAfterProfileOpen()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-profile-provider-added-refresh-tests", Guid.NewGuid().ToString("N"));

        try
        {
            using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
            await runtime.CreateSessionAsync("test_tool");
            var subagentService = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = subagentService.CreateSubagent("Late Specialist");
            subagentService.SaveSubagent(
                subagent.SubagentId,
                subagent.DisplayName,
                "Handles work after the profile view is already open.",
                subagent.Instructions,
                null,
                null,
                []);
            using var viewModel = new AgentProfilesViewModel(runtime.ProfileService);

            await WaitUntilAsync(() => viewModel.SelectedProfile is not null);
            Assert.Empty(viewModel.PackageCapabilities);

            runtime.ExtensionCatalog.AddExtension(
                PackageExtensionPoints.ProfileSelectableCapabilityProviders,
                new SubagentFeature(subagentService, runtime.ExtensionCatalog));

            await WaitUntilAsync(() => viewModel.PackageCapabilities.Any(capability => capability.CapabilityId == subagent.SubagentId));
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
    public void AgentProfileSelectableCapabilityChangeObserver_UnsubscribesWhenDisposed()
    {
        var catalog = new TestExtensionCatalog();
        var provider = new MutableSelectableCapabilityProvider();
        using var observer = new AgentProfileSelectableCapabilityChangeObserver(catalog);
        var changeCount = 0;
        observer.Changed += () => changeCount++;

        catalog.AddExtension(PackageExtensionPoints.ProfileSelectableCapabilityProviders, provider);
        provider.RaiseChanged();

        Assert.Equal(2, changeCount);

        observer.Dispose();
        provider.RaiseChanged();

        Assert.Equal(2, changeCount);
    }

    [Fact]
    public async Task SubagentsViewModel_RefreshesMcpCapabilities_WhenServerCatalogChanges()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-mcp-subagent-refresh-tests", Guid.NewGuid().ToString("N"));

        try
        {
            using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
            var subagentService = new SubagentService(new SubagentStore(new TestPackageContext(Path.Combine(rootPath, "subagents"))));
            var subagent = subagentService.CreateSubagent("Worker");
            subagentService.SaveSubagent(
                subagent.SubagentId,
                subagent.DisplayName,
                "Handles delegated work.",
                subagent.Instructions,
                null,
                null,
                []);
            var serverCatalogService = new McpServerCatalogService(new TestPackageContext(Path.Combine(rootPath, "mcp")));
            await using var connectionManager = new McpClientConnectionManager(NullLoggerFactory.Instance);
            runtime.ExtensionCatalog.AddExtension(
                PackageExtensionPoints.ProfileSelectableCapabilityProviders,
                new McpToolSource(serverCatalogService, connectionManager));
            using var viewModel = new SubagentsViewModel(subagentService, runtime.ExtensionCatalog);
            await viewModel.InitializeAsync();

            await WaitUntilAsync(() => viewModel.SelectedSubagent is not null);
            Assert.DoesNotContain(viewModel.CapabilityOptions, capability => capability.SourceId == "mcp");

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

            await serverCatalogService.SaveServerAsync(server, new Dictionary<string, string>(), new Dictionary<string, string>());

            await WaitUntilAsync(() => viewModel.CapabilityOptions.Any(capability => capability.CapabilityId == server.ServerId));
            Assert.Contains(viewModel.CapabilityGroups, group => group.Title == "MCP Servers");

            var renamedServer = server with
            {
                DisplayName = "Renamed MCP",
                Description = "Renamed MCP tools.",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await serverCatalogService.SaveServerAsync(renamedServer, new Dictionary<string, string>(), new Dictionary<string, string>());

            await WaitUntilAsync(() => viewModel.CapabilityOptions.Any(capability => capability.CapabilityId == server.ServerId
                                                                                    && capability.DisplayName == "Renamed MCP"));

            await serverCatalogService.DeleteServerAsync(server.ServerId);

            await WaitUntilAsync(() => viewModel.CapabilityOptions.All(capability => capability.CapabilityId != server.ServerId));
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
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-mcp-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var serverCatalogService = new McpServerCatalogService(new TestPackageContext(rootPath));
            await serverCatalogService.SaveServerAsync(CreateMcpServer("local-mcp", "local_mcp", "Local MCP"), new Dictionary<string, string>(), new Dictionary<string, string>());
            await using var connectionManager = new McpClientConnectionManager(NullLoggerFactory.Instance);
            using var viewModel = new AgentMcpSettingsViewModel(serverCatalogService, connectionManager)
            {
                IsCompactLayout = true,
            };

            await WaitUntilAsync(() => viewModel.Servers.Count == 1);

            Assert.Null(viewModel.SelectedServer);
            Assert.False(viewModel.IsEditorActive);
            Assert.True(viewModel.ShowCompactList);

            viewModel.ActivateServer(viewModel.Servers.Single(server => server.ServerId == "local-mcp"));
            await WaitUntilAsync(() => viewModel.Name == "local_mcp");

            Assert.True(viewModel.IsEditorActive);
            Assert.Equal("local-mcp", viewModel.SelectedServer?.ServerId);
            Assert.True(viewModel.ShowCompactEditor);

            viewModel.BackToServerListCommand.Execute(null);

            Assert.False(viewModel.IsEditorActive);
            Assert.Null(viewModel.SelectedServer);
            Assert.True(viewModel.ShowCompactList);

            viewModel.IsCompactLayout = false;

            Assert.Equal("local-mcp", viewModel.SelectedServer?.ServerId);
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
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-mcp-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var serverCatalogService = new McpServerCatalogService(new TestPackageContext(rootPath));
            await serverCatalogService.SaveServerAsync(CreateMcpServer("local-mcp", "local_mcp", "Local MCP"), new Dictionary<string, string>(), new Dictionary<string, string>());
            await using var connectionManager = new McpClientConnectionManager(NullLoggerFactory.Instance);
            using var viewModel = new AgentMcpSettingsViewModel(serverCatalogService, connectionManager)
            {
                IsCompactLayout = true,
            };
            await WaitUntilAsync(() => viewModel.Servers.Count == 1);
            viewModel.ActivateServer(viewModel.Servers.Single(server => server.ServerId == "local-mcp"));
            await WaitUntilAsync(() => viewModel.Name == "local_mcp");

            await viewModel.SaveCommand.ExecuteAsync(null);

            Assert.False(viewModel.IsEditorActive);
            Assert.Null(viewModel.SelectedServer);
            Assert.Empty(viewModel.StatusText);
            Assert.Contains(await serverCatalogService.ListServersAsync(), server => server.ServerId == "local-mcp");
        }
        finally
        {
            TryDeleteDirectory(rootPath);
        }
    }

    [Fact]
    public async Task AgentMcpSettingsViewModel_Save_WideLayout_KeepsEditorLoadedAndAutoClearsSuccess()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "sunder-mcp-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var serverCatalogService = new McpServerCatalogService(new TestPackageContext(rootPath));
            await serverCatalogService.SaveServerAsync(CreateMcpServer("local-mcp", "local_mcp", "Local MCP"), new Dictionary<string, string>(), new Dictionary<string, string>());
            await using var connectionManager = new McpClientConnectionManager(NullLoggerFactory.Instance);
            using var viewModel = new AgentMcpSettingsViewModel(serverCatalogService, connectionManager);

            await WaitUntilAsync(() => viewModel.SelectedServer?.ServerId == "local-mcp" && viewModel.Name == "local_mcp");

            await viewModel.SaveCommand.ExecuteAsync(null);

            Assert.False(viewModel.IsEditorActive);
            Assert.Equal("local-mcp", viewModel.SelectedServer?.ServerId);
            Assert.Equal("local_mcp", viewModel.Name);
            Assert.Contains("Saved MCP server", viewModel.StatusText, StringComparison.Ordinal);
            Assert.True(viewModel.IsStatusSuccess);
            Assert.False(viewModel.IsStatusWarning);
            Assert.False(viewModel.IsStatusError);

            await WaitUntilAsync(() => string.IsNullOrWhiteSpace(viewModel.StatusText), TimeSpan.FromSeconds(4));

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
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
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
            chatModelSettingsJson: "{\"reasoningVariantId\":\"high\"}");

        var chatBinding = runtime.ProfileService.GetChatBinding(profile.ProfileId);

        Assert.NotNull(chatBinding);
        using var settingsDocument = JsonDocument.Parse(chatBinding.SettingsJson!);
        Assert.Equal("high", settingsDocument.RootElement.GetProperty("reasoningVariantId").GetString());
    }

    [Fact]
    public async Task AgentRunCoordinator_AppliesProfileReasoningVariantToChatOptions()
    {
        var provider = new ScriptedProvider(
            (request, _) =>
            {
                Assert.Equal(ReasoningEffort.High, request.ReasoningEffort);
                return Complete("done");
            },
            models:
            [
                new AgentModelDescriptor(
                    "test-model",
                    "Test Model",
                    128_000,
                    4_096,
                    IsRecommended: true,
                    Variants: [new AgentModelVariantDescriptor("high", "High", ReasoningEffort: AgentReasoningEffort.High)])
            ]);
        using var runtime = AgentTestRuntime.Create(provider);
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
            chatModelSettingsJson: "{\"reasoningVariantId\":\"high\"}");
        var workspace = runtime.WorkspaceService.CreateWorkspace("Test Workspace");
        var session = runtime.SessionService.CreateSession("Test Session");

        await runtime.RunCoordinator.QueueUserMessageAsync(session.SessionId, profile.ProfileId, "Use high reasoning.", workspace.WorkspaceId);

        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task AgentRunCoordinator_IgnoresStaleProfileReasoningVariant()
    {
        var provider = new ScriptedProvider(
            (request, _) =>
            {
                Assert.Null(request.ReasoningEffort);
                return Complete("done");
            },
            models:
            [
                new AgentModelDescriptor(
                    "test-model",
                    "Test Model",
                    128_000,
                    4_096,
                    IsRecommended: true,
                    Variants: [new AgentModelVariantDescriptor("low", "Low", ReasoningEffort: AgentReasoningEffort.Low)])
            ]);
        using var runtime = AgentTestRuntime.Create(provider);
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
            chatModelSettingsJson: "{\"reasoningVariantId\":\"high\"}");
        var workspace = runtime.WorkspaceService.CreateWorkspace("Test Workspace");
        var session = runtime.SessionService.CreateSession("Test Session");

        await runtime.RunCoordinator.QueueUserMessageAsync(session.SessionId, profile.ProfileId, "Use default reasoning.", workspace.WorkspaceId);

        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task AgentChatViewModel_HidesChildSessionsFromSessionSelector()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");

        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);

        Assert.Contains(viewModel.Sessions, session => session.SessionId == parentSessionId);
        Assert.DoesNotContain(viewModel.Sessions, session => session.SessionId == childSession.SessionId);
        Assert.Equal(parentSessionId, viewModel.SelectedSession?.SessionId);
    }

    [Fact]
    public async Task AgentChatViewModel_OpenChildSession_OpensSubsessionsViewWithoutChangingSelector()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        runtime.SessionService.AppendTextTurn(childSession.SessionId, AgentMessageRole.Assistant, "Child transcript content.");
        var shellViewService = new CapturingShellViewService();

        using var viewModel = new AgentChatViewModel(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator,
            shellViewService: shellViewService);

        await viewModel.OpenChildSessionCommand.ExecuteAsync(new AgentChildSessionLinkViewModel(childSession.SessionId, childSession.Title, "Exploration subagent"));

        Assert.Equal(parentSessionId, viewModel.SelectedSession?.SessionId);
        Assert.Equal(parentSessionId, viewModel.DisplayedSession?.SessionId);
        Assert.True(viewModel.ShowCollapsedComposer);
        Assert.DoesNotContain(viewModel.Sessions, session => session.SessionId == childSession.SessionId);
        Assert.Equal(SubagentConstants.SubsessionsViewId, shellViewService.OpenedViewId);
        Assert.NotNull(shellViewService.Parameters);
        Assert.Equal(childSession.SessionId.ToString("D"), shellViewService.Parameters![SubagentConstants.SubsessionNavigationSessionIdKey]);
    }

    [Fact]
    public async Task AgentChatViewModel_RestoresChildSelectionAsRootSession()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        var selectionRootPath = Path.Combine(Path.GetTempPath(), "sunder-agent-selection-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var selectionContext = new TestPackageContext(selectionRootPath);
            var selectionState = new AgentChatSelectionStateService(selectionContext);
            selectionState.SaveSelectedWorkspaceId(runtime.CurrentWorkspaceId);
            selectionState.SaveSelectedSessionId(childSession.SessionId);
            selectionState.SaveSelectedProfileId(runtime.CurrentProfileId);

            using var viewModel = new AgentChatViewModel(
                runtime.ProfileService,
                runtime.WorkspaceService,
                runtime.SessionService,
                runtime.PermissionService,
                runtime.RunCoordinator,
                selectionState);

            Assert.Equal(parentSessionId, viewModel.SelectedSession?.SessionId);
            Assert.Contains(viewModel.Sessions, session => session.SessionId == parentSessionId);
            Assert.DoesNotContain(viewModel.Sessions, session => session.SessionId == childSession.SessionId);
        }
        finally
        {
            try
            {
                if (Directory.Exists(selectionRootPath))
                {
                    Directory.Delete(selectionRootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public void AgentToolInvocationRow_ExposesChildSessionLinkFromTaskResult()
    {
        var now = DateTimeOffset.UtcNow;
        var childSessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var argumentsJson = JsonSerializer.Serialize(new
        {
            description = "Explore current repository state",
            subagent_type = "Exploration"
        });
        var structuredPayloadJson = JsonSerializer.Serialize(new
        {
            childSessionId,
            childSessionTitle = "Explore current repository state",
            subagentName = "Exploration"
        });
        var turn = new AgentTurnRecord(turnId, Guid.NewGuid(), AgentMessageRole.Tool, AgentTurnKind.ToolResult, [], now, now);
        var item = new AgentTurnItemRecord(
            Guid.NewGuid(),
            turnId,
            0,
            AgentTurnItemKind.ToolResult,
            "done",
            "task-call",
            "task",
            argumentsJson,
            "Subagent completed.",
            structuredPayloadJson,
            null,
            false,
            false,
            null,
            childSessionId.ToString("N"));

        var row = new AgentToolInvocationRowViewModel(turn, item, new AgentToolPresentationService());

        Assert.True(row.HasChildSessionLink);
        Assert.NotNull(row.ChildSessionLink);
        Assert.Equal(childSessionId, row.ChildSessionLink!.SessionId);
        Assert.Equal("Explore current repository state", row.ChildSessionLink.Title);
        Assert.Equal("Exploration subagent", row.ChildSessionLink.Subtitle);
        Assert.Equal("Exploration subagent · Explore current repository state", row.ChildSessionLink.DisplayText);
    }

    [Fact]
    public void AgentToolInvocationRow_ExposesChildSessionLinksFromDelegateTasksResult()
    {
        var now = DateTimeOffset.UtcNow;
        var firstChildSessionId = Guid.NewGuid();
        var secondChildSessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var argumentsJson = JsonSerializer.Serialize(new
        {
            tasks = new[]
            {
                new { description = "Explore repository", subagent_type = "Exploration" },
                new { description = "Review test risks", subagent_type = "Reviewer" },
            }
        });
        var structuredPayloadJson = JsonSerializer.Serialize(new
        {
            tasks = new[]
            {
                new { childSessionId = firstChildSessionId, childSessionTitle = "Explore repository", subagentName = "Exploration" },
                new { childSessionId = secondChildSessionId, childSessionTitle = "Review test risks", subagentName = "Reviewer" },
            }
        });
        var turn = new AgentTurnRecord(turnId, Guid.NewGuid(), AgentMessageRole.Tool, AgentTurnKind.ToolResult, [], now, now);
        var item = new AgentTurnItemRecord(
            Guid.NewGuid(),
            turnId,
            0,
            AgentTurnItemKind.ToolResult,
            "done",
            "delegate-call",
            SubagentConstants.DelegateTasksToolId,
            argumentsJson,
            "Delegated subagent tasks completed.",
            structuredPayloadJson,
            null,
            false,
            false,
            null,
            null);

        var row = new AgentToolInvocationRowViewModel(turn, item, new AgentToolPresentationService());

        Assert.True(row.HasChildSessionLinks);
        Assert.Equal(2, row.ChildSessionLinks.Count);
        Assert.Equal(firstChildSessionId, row.ChildSessionLinks[0].SessionId);
        Assert.Equal("Exploration subagent · Explore repository", row.ChildSessionLinks[0].DisplayText);
        Assert.Equal(secondChildSessionId, row.ChildSessionLinks[1].SessionId);
        Assert.Equal("Reviewer subagent · Review test risks", row.ChildSessionLinks[1].DisplayText);
    }

    [Fact]
    public async Task AgentChatViewModel_TaskRowLinksChildSessionBeforeTaskResult()
    {
        const string toolId = "fetch_page";
        const string toolCallId = "task-call";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var argumentsJson = JsonSerializer.Serialize(new
        {
            description = "Explore current repository state",
            prompt = "Inspect the repository.",
            subagent_type = "Exploration",
        });
        runtime.SessionService.AppendToolCallTurn(parentSessionId, AgentMessageRole.Assistant, toolCallId, "task", argumentsJson);
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            parentRunId: Guid.NewGuid(),
            parentRunRevision: 1,
            parentToolCallId: toolCallId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");

        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);

        var row = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
        Assert.True(row.HasChildSessionLink);
        Assert.NotNull(row.ChildSessionLink);
        Assert.Equal(childSession.SessionId, row.ChildSessionLink!.SessionId);
        Assert.Equal("Explore current repository state", row.ChildSessionLink.Title);
    }

    [Fact]
    public async Task AgentChatViewModel_ChildSessionLinksShowAndRefreshStatus()
    {
        const string toolId = "fetch_page";
        const string toolCallId = "task-call";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        runtime.SessionService.AppendToolCallTurn(parentSessionId, AgentMessageRole.Assistant, toolCallId, "task", "{\"description\":\"Explore\",\"prompt\":\"Inspect.\",\"subagent_type\":\"Explore\"}");
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            parentRunId: Guid.NewGuid(),
            parentRunRevision: 1,
            parentToolCallId: toolCallId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        var runRevision = runtime.SessionService.GetNextRunRevision(childSession.SessionId);
        runtime.SessionService.SaveCheckpoint(childSession.SessionId, runRevision, AgentRunStatus.Running, "Child running.");
        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);
        var row = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
        var link = Assert.Single(row.ChildSessionLinks);

        Assert.Equal("Running", link.StatusText);
        Assert.Equal("i", link.StatusIconText);

        runtime.SessionService.SaveCheckpoint(childSession.SessionId, runRevision, AgentRunStatus.Completed, "Child done.");

        var refreshedLink = Assert.Single(row.ChildSessionLinks);
        Assert.Same(link, refreshedLink);
        Assert.Equal("Done", refreshedLink.StatusText);
        Assert.Equal("✓", refreshedLink.StatusIconText);
    }

    [Fact]
    public async Task AgentChatViewModel_DelegateTasksRowLinksChildSessionsBeforeTaskResult()
    {
        const string toolId = "fetch_page";
        const string toolCallId = "delegate-call";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var argumentsJson = JsonSerializer.Serialize(new
        {
            tasks = new[]
            {
                new { description = "Explore current repository state", prompt = "Inspect the repository.", subagent_type = "Exploration" },
                new { description = "Review test coverage", prompt = "Inspect tests.", subagent_type = "Reviewer" },
            }
        });
        runtime.SessionService.AppendToolCallTurn(parentSessionId, AgentMessageRole.Assistant, toolCallId, SubagentConstants.DelegateTasksToolId, argumentsJson);
        var firstChild = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            parentRunId: Guid.NewGuid(),
            parentRunRevision: 1,
            parentToolCallId: toolCallId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        var secondChild = runtime.SessionService.CreateSession(
            "Review test coverage",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            parentRunId: Guid.NewGuid(),
            parentRunRevision: 1,
            parentToolCallId: toolCallId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");

        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);

        var row = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
        Assert.True(row.HasChildSessionLinks);
        Assert.Equal(2, row.ChildSessionLinks.Count);
        Assert.Equal(firstChild.SessionId, row.ChildSessionLinks[0].SessionId);
        Assert.Equal(secondChild.SessionId, row.ChildSessionLinks[1].SessionId);
    }

    [Fact]
    public async Task SubsessionsViewModel_NavigationSelectsChildSessionAndLoadsTranscript()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        runtime.SessionService.AppendTextTurn(childSession.SessionId, AgentMessageRole.Assistant, "Child transcript content.");
        using var viewModel = new SubsessionsViewModel(runtime.ExtensionCatalog);

        await viewModel.OnNavigatedToAsync(new PackageViewNavigationContext(
            SubagentConstants.SubsessionsViewId,
            new Dictionary<string, string?>
            {
                [SubagentConstants.SubsessionNavigationSessionIdKey] = childSession.SessionId.ToString("D"),
            }));

        Assert.Equal(childSession.SessionId, viewModel.SelectedSubsession?.SessionId);
        Assert.DoesNotContain(viewModel.Subsessions, session => session.SessionId == parentSessionId);
        Assert.Contains(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>(), row => row.Content == "Child transcript content.");

        runtime.SessionService.AppendTextTurn(childSession.SessionId, AgentMessageRole.Assistant, "Live child update.");

        Assert.Contains(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>(), row => row.Content == "Live child update.");
    }

    [Fact]
    public async Task SubsessionsViewModel_RepeatedNavigationToSelectedSubsessionIsNoOp()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var firstChildSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        var secondChildSession = runtime.SessionService.CreateSession(
            "Review implementation details",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        runtime.SessionService.AppendTextTurn(firstChildSession.SessionId, AgentMessageRole.Assistant, "First transcript content.");
        runtime.SessionService.AppendTextTurn(secondChildSession.SessionId, AgentMessageRole.Assistant, "Second transcript content.");
        using var viewModel = new SubsessionsViewModel(runtime.ExtensionCatalog);
        var firstNavigation = new PackageViewNavigationContext(
            SubagentConstants.SubsessionsViewId,
            new Dictionary<string, string?>
            {
                [SubagentConstants.SubsessionNavigationSessionIdKey] = firstChildSession.SessionId.ToString("D"),
            });
        var secondNavigation = new PackageViewNavigationContext(
            SubagentConstants.SubsessionsViewId,
            new Dictionary<string, string?>
            {
                [SubagentConstants.SubsessionNavigationSessionIdKey] = secondChildSession.SessionId.ToString("D"),
            });

        await viewModel.OnNavigatedToAsync(firstNavigation);
        var selectedSubsession = viewModel.SelectedSubsession;
        var transcriptRow = Assert.Single(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>());

        await viewModel.OnNavigatedToAsync(firstNavigation);

        Assert.Same(selectedSubsession, viewModel.SelectedSubsession);
        Assert.Same(transcriptRow, Assert.Single(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>()));

        await viewModel.OnNavigatedToAsync(secondNavigation);

        Assert.Equal(secondChildSession.SessionId, viewModel.SelectedSubsession?.SessionId);
        Assert.NotSame(selectedSubsession, viewModel.SelectedSubsession);
        Assert.Contains(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>(), row => row.Content == "Second transcript content.");
    }

    [Fact]
    public async Task SubsessionsViewModel_SessionChangedUpdatesStatusWithoutReloadingTranscript()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        runtime.SessionService.AppendTextTurn(childSession.SessionId, AgentMessageRole.User, "Child task request.");
        using var viewModel = new SubsessionsViewModel(runtime.ExtensionCatalog);
        await viewModel.OnNavigatedToAsync(new PackageViewNavigationContext(
            SubagentConstants.SubsessionsViewId,
            new Dictionary<string, string?>
            {
                [SubagentConstants.SubsessionNavigationSessionIdKey] = childSession.SessionId.ToString("D"),
            }));
        var selectedSubsession = viewModel.SelectedSubsession;
        var transcriptRow = Assert.Single(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>());
        var runRevision = runtime.SessionService.GetNextRunRevision(childSession.SessionId);

        runtime.SessionService.SaveCheckpoint(childSession.SessionId, runRevision, AgentRunStatus.Running, "Executing tool 'task'.");

        Assert.Same(selectedSubsession, viewModel.SelectedSubsession);
        Assert.Same(transcriptRow, Assert.Single(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>()));
        var activityRow = Assert.Single(viewModel.Messages.OfType<SubsessionActivityTranscriptRowViewModel>());
        Assert.StartsWith("Running Task", activityRow.ThinkingText, StringComparison.Ordinal);
        Assert.True(viewModel.SelectedSubsession?.IsRunActive);

        runtime.SessionService.SaveCheckpoint(childSession.SessionId, runRevision, AgentRunStatus.Completed, "Done.");

        Assert.Same(selectedSubsession, viewModel.SelectedSubsession);
        Assert.Same(transcriptRow, Assert.Single(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>()));
        Assert.DoesNotContain(viewModel.Messages, row => row is SubsessionActivityTranscriptRowViewModel);
        Assert.False(viewModel.SelectedSubsession?.IsRunActive);
    }

    [Fact]
    public async Task SubsessionListItemViewModel_ApplyCheckpoint_DoesNotTouchAvaloniaResourcesOffUiThread()
    {
        var now = DateTimeOffset.UtcNow;
        var session = new AgentSessionRecord(Guid.NewGuid(), "Background Child", AgentSessionState.Active, now, now, AgentKind: "subagent");
        var checkpoint = new AgentRunCheckpointRecord(Guid.NewGuid(), session.SessionId, 1, AgentRunStatus.Running, "Running.", now);
        SubsessionListItemViewModel? item = null;

        var exception = await Record.ExceptionAsync(async () =>
        {
            item = await Task.Run(() => new SubsessionListItemViewModel(session, "Subagent", checkpoint));
        });

        Assert.Null(exception);
        Assert.NotNull(item);
        Assert.Equal("Running", item!.StatusBadgeText);
    }

    [Fact]
    public async Task SubsessionsViewModel_TurnChangedAppendsWithoutReloadingExistingRows()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        runtime.SessionService.AppendTextTurn(childSession.SessionId, AgentMessageRole.Assistant, "First transcript content.");
        using var viewModel = new SubsessionsViewModel(runtime.ExtensionCatalog);
        await viewModel.OnNavigatedToAsync(new PackageViewNavigationContext(
            SubagentConstants.SubsessionsViewId,
            new Dictionary<string, string?>
            {
                [SubagentConstants.SubsessionNavigationSessionIdKey] = childSession.SessionId.ToString("D"),
            }));
        var firstRow = Assert.Single(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>());

        runtime.SessionService.AppendTextTurn(childSession.SessionId, AgentMessageRole.Assistant, "Second transcript content.");

        var textRows = viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>().ToArray();
        Assert.Equal(2, textRows.Length);
        Assert.Same(firstRow, textRows[0]);
        Assert.Equal("Second transcript content.", textRows[1].Content);
    }

    [Fact]
    public async Task SubsessionsViewModel_PreservesExpandedToolRowsWhenSelectedSubsessionReorders()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var firstChildSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        runtime.SessionService.AppendToolCallTurn(firstChildSession.SessionId, AgentMessageRole.Assistant, "call-1", "read", "{\"path\":\"/workspace/README.md\"}");
        runtime.SessionService.CreateSession(
            "Review implementation details",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent");
        using var viewModel = new SubsessionsViewModel(runtime.ExtensionCatalog);
        await viewModel.OnNavigatedToAsync(new PackageViewNavigationContext(
            SubagentConstants.SubsessionsViewId,
            new Dictionary<string, string?>
            {
                [SubagentConstants.SubsessionNavigationSessionIdKey] = firstChildSession.SessionId.ToString("D"),
            }));
        var toolRow = Assert.Single(viewModel.Messages.OfType<SubsessionToolInvocationRowViewModel>());
        toolRow.IsExpanded = true;
        var observedMove = false;
        viewModel.Subsessions.CollectionChanged += (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Move)
            {
                return;
            }

            observedMove = true;
            viewModel.SelectedSubsession = null;
        };

        runtime.SessionService.SaveCheckpoint(firstChildSession.SessionId, runtime.SessionService.GetNextRunRevision(firstChildSession.SessionId), AgentRunStatus.Running, "Child running.");

        Assert.True(observedMove);
        Assert.Equal(firstChildSession.SessionId, viewModel.SelectedSubsession?.SessionId);
        var preservedToolRow = Assert.Single(viewModel.Messages.OfType<SubsessionToolInvocationRowViewModel>());
        Assert.Same(toolRow, preservedToolRow);
        Assert.True(preservedToolRow.IsExpanded);
    }

    [Fact]
    public async Task AgentChatViewModel_RestoresPersistedWorkspaceAndSession_WhenStillValid()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var firstSessionId = await runtime.CreateSessionAsync(toolId);
        var secondProfile = await runtime.ProfileService.CreateProfileAsync("Second Profile");
        var secondWorkspace = runtime.WorkspaceService.CreateWorkspace("Second Workspace");
        var secondSession = runtime.SessionService.CreateSession("Second Session");
        var selectionRootPath = Path.Combine(Path.GetTempPath(), "sunder-agent-selection-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var selectionContext = new TestPackageContext(selectionRootPath);
            var selectionState = new AgentChatSelectionStateService(selectionContext);
            selectionState.SaveSelectedWorkspaceId(secondWorkspace.WorkspaceId);
            selectionState.SaveSelectedSessionId(secondSession.SessionId);
            selectionState.SaveSelectedProfileId(secondProfile.ProfileId);

            using var viewModel = new AgentChatViewModel(
                runtime.ProfileService,
                runtime.WorkspaceService,
                runtime.SessionService,
                runtime.PermissionService,
                runtime.RunCoordinator,
                selectionState);

            Assert.Equal(secondWorkspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
            Assert.Equal(secondSession.SessionId, viewModel.SelectedSession?.SessionId);
            Assert.Equal(secondProfile.ProfileId, viewModel.SelectedProfile?.ProfileId);
            Assert.NotEqual(firstSessionId, viewModel.SelectedSession?.SessionId);
        }
        finally
        {
            try
            {
                if (Directory.Exists(selectionRootPath))
                {
                    Directory.Delete(selectionRootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public async Task AgentChatViewModel_PreservesDraftPerSession_WhenSwitchingSessions()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionA = await runtime.CreateSessionAsync(toolId);
        var sessionB = runtime.SessionService.CreateSession("Test Session B").SessionId;
        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);

        viewModel.SelectedSession = viewModel.Sessions.Single(session => session.SessionId == sessionA);
        viewModel.DraftMessage = "draft for session A";

        viewModel.SelectedSession = viewModel.Sessions.Single(session => session.SessionId == sessionB);
        viewModel.DraftMessage = "draft for session B";

        viewModel.SelectedSession = viewModel.Sessions.Single(session => session.SessionId == sessionA);
        Assert.Equal("draft for session A", viewModel.DraftMessage);

        viewModel.SelectedSession = viewModel.Sessions.Single(session => session.SessionId == sessionB);
        Assert.Equal("draft for session B", viewModel.DraftMessage);
    }

    [Fact]
    public async Task AgentChatViewModel_MarksBackgroundSessionUnread_WithoutOverwritingSelectedSessionState()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionA = await runtime.CreateSessionAsync(toolId);
        var sessionB = runtime.SessionService.CreateSession("Test Session B").SessionId;
        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);

        var sessionAItem = viewModel.Sessions.Single(session => session.SessionId == sessionA);
        var sessionBItem = viewModel.Sessions.Single(session => session.SessionId == sessionB);

        viewModel.SelectedSession = sessionAItem;
        viewModel.DraftMessage = "keep selected draft";

        var selectedRevision = runtime.SessionService.GetNextRunRevision(sessionA);
        runtime.SessionService.SaveCheckpoint(sessionA, selectedRevision, AgentRunStatus.Completed, "Primary session completed.");

        Assert.Contains("Primary session completed.", viewModel.StatusText, StringComparison.Ordinal);

        var backgroundRevision = runtime.SessionService.GetNextRunRevision(sessionB);
        runtime.SessionService.SaveCheckpoint(sessionB, backgroundRevision, AgentRunStatus.Running, "Background session is still running.");

        Assert.True(sessionBItem.HasUnreadActivity);
        Assert.Equal("keep selected draft", viewModel.DraftMessage);
        Assert.Contains("Primary session completed.", viewModel.StatusText, StringComparison.Ordinal);

        viewModel.SelectedSession = sessionBItem;

        Assert.False(sessionBItem.HasUnreadActivity);
        Assert.Equal(string.Empty, viewModel.DraftMessage);
        Assert.Contains("Background session is still running.", viewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentChatViewModel_PreservesSelectedSession_WhenActivityReordersSessionList()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionA = await runtime.CreateSessionAsync(toolId);
        var sessionB = runtime.SessionService.CreateSession("Test Session B").SessionId;
        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);
        var sessionAItem = viewModel.Sessions.Single(session => session.SessionId == sessionA);
        viewModel.SelectedSession = sessionAItem;
        viewModel.DraftMessage = "keep selected draft";
        var observedMove = false;
        viewModel.Sessions.CollectionChanged += (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Move)
            {
                return;
            }

            observedMove = true;
            viewModel.SelectedSession = null;
        };

        runtime.SessionService.SaveCheckpoint(sessionA, runtime.SessionService.GetNextRunRevision(sessionA), AgentRunStatus.Running, "Selected session is running.");

        Assert.True(observedMove);
        Assert.Equal(sessionA, viewModel.SelectedSession?.SessionId);
        Assert.Equal(sessionA, viewModel.DisplayedSession?.SessionId);
        Assert.Equal("keep selected draft", viewModel.DraftMessage);
        Assert.False(viewModel.ShowSetupInstructions);
        Assert.True(viewModel.ShowCollapsedComposer);
        Assert.Equal(0, viewModel.Sessions.IndexOf(viewModel.Sessions.Single(session => session.SessionId == sessionA)));
        Assert.Contains(viewModel.Sessions, session => session.SessionId == sessionB);
    }

    [Fact]
    public async Task AgentChatViewModel_LoadsRecentTranscriptWindowAndOlderRowsOnDemand()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        for (var index = 0; index < 120; index++)
        {
            runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.User, $"message-{index:000}");
        }

        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);

        Assert.Equal(100, viewModel.Messages.Count);
        Assert.True(viewModel.HasOlderTranscriptRows);
        Assert.Equal("message-020", Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[0]).Content);
        Assert.Equal("message-119", Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[^1]).Content);

        var loaded = await viewModel.LoadOlderTranscriptRowsAsync();

        Assert.True(loaded);
        Assert.Equal(120, viewModel.Messages.Count);
        Assert.False(viewModel.HasOlderTranscriptRows);
        Assert.Equal("message-000", Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[0]).Content);
    }

    [Fact]
    public async Task AgentChatViewModel_UpdatesStreamingAssistantTurnInPlace()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);

        var turn = runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.Assistant, "partial");
        var row = Assert.IsType<AgentTextTranscriptRowViewModel>(Assert.Single(viewModel.Messages));

        runtime.SessionService.UpdateTextTurn(turn.TurnId, "partial plus more");

        var updatedRow = Assert.IsType<AgentTextTranscriptRowViewModel>(Assert.Single(viewModel.Messages));
        Assert.Same(row, updatedRow);
        Assert.Equal("partial plus more", updatedRow.Content);
    }

    [Fact]
    public async Task AgentChatViewModel_WorkspaceSave_PreservesSelectedSessionAndTranscriptRows()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.Assistant, "existing response");
        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);
        Assert.NotNull(viewModel.SelectedSession);
        var selectedSession = viewModel.SelectedSession;
        var transcriptRow = Assert.Single(viewModel.Messages);
        var workspaceId = viewModel.SelectedWorkspace!.WorkspaceId;
        var transcriptChangedCount = 0;
        viewModel.Workspaces.CollectionChanged += (_, args) =>
        {
            if (args.Action is NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Reset)
            {
                viewModel.SelectedWorkspace = null;
            }
        };
        viewModel.TranscriptChanged += () => transcriptChangedCount++;

        runtime.WorkspaceService.SaveWorkspace(workspaceId, "Renamed Workspace", "Updated description");

        Assert.Same(selectedSession, viewModel.SelectedSession);
        Assert.Same(selectedSession, Assert.Single(viewModel.Sessions));
        Assert.Same(transcriptRow, Assert.Single(viewModel.Messages));
        Assert.Equal(0, transcriptChangedCount);
        Assert.Equal("Renamed Workspace", viewModel.SelectedWorkspace?.DisplayName);
        Assert.Contains(viewModel.Workspaces, workspace =>
            string.Equals(workspace.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
            && workspace.DisplayName == "Renamed Workspace");
    }

    [Fact]
    public async Task AgentChatViewModel_SendMessageCommand_PreservesExistingExpandedToolRows()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        runtime.SessionService.AppendToolCallTurn(sessionId, AgentMessageRole.Assistant, "call-1", toolId, "{\"url\":\"https://example.com\"}");
        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);
        var toolRow = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
        toolRow.IsExpanded = true;

        viewModel.DraftMessage = "continue";
        await viewModel.SendMessageCommand.ExecuteAsync(null);

        var preservedToolRow = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
        Assert.Same(toolRow, preservedToolRow);
        Assert.True(preservedToolRow.IsExpanded);
        Assert.True(preservedToolRow.ShowDetails);
        Assert.Contains(viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>(), row => row.Content == "done");
    }

    [Fact]
    public async Task AgentChatViewModel_StopRunCommand_PreservesExistingExpandedToolRows()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        runtime.SessionService.AppendToolCallTurn(sessionId, AgentMessageRole.Assistant, "call-1", toolId, "{\"url\":\"https://example.com\"}");
        runtime.SessionService.SaveCheckpoint(sessionId, runtime.SessionService.GetNextRunRevision(sessionId), AgentRunStatus.Running, "Thinking.");
        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);
        var toolRow = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
        toolRow.IsExpanded = true;

        await viewModel.StopRunCommand.ExecuteAsync(null);

        var preservedToolRow = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
        Assert.Same(toolRow, preservedToolRow);
        Assert.True(preservedToolRow.IsExpanded);
        Assert.True(preservedToolRow.ShowDetails);
        Assert.DoesNotContain(viewModel.Messages, row => row is AgentActivityTranscriptRowViewModel);
    }

    [Fact]
    public async Task AgentChatViewModel_ShowsActivityRowOnlyWhileSelectedSessionRuns()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);

        runtime.SessionService.SaveCheckpoint(sessionId, runtime.SessionService.GetNextRunRevision(sessionId), AgentRunStatus.Running, "Thinking.");

        Assert.IsType<AgentActivityTranscriptRowViewModel>(Assert.Single(viewModel.Messages));

        runtime.SessionService.SaveCheckpoint(sessionId, runtime.SessionService.GetNextRunRevision(sessionId), AgentRunStatus.Completed, "Done.");

        Assert.Empty(viewModel.Messages);
    }

    [Fact]
    public async Task AgentChatViewModel_RemovesActivityRowWhenAssistantTextStarts()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);

        runtime.SessionService.SaveCheckpoint(sessionId, runtime.SessionService.GetNextRunRevision(sessionId), AgentRunStatus.Running, "Thinking.");

        Assert.IsType<AgentActivityTranscriptRowViewModel>(Assert.Single(viewModel.Messages));

        runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.Assistant, "partial response");

        var textRow = Assert.IsType<AgentTextTranscriptRowViewModel>(Assert.Single(viewModel.Messages));
        Assert.Equal("partial response", textRow.Content);
    }

    [Fact]
    public async Task AgentChatViewModel_RemovesActivityRowWhenToolActivityStarts()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);

        runtime.SessionService.SaveCheckpoint(sessionId, runtime.SessionService.GetNextRunRevision(sessionId), AgentRunStatus.Running, "Thinking.");

        Assert.IsType<AgentActivityTranscriptRowViewModel>(Assert.Single(viewModel.Messages));

        runtime.SessionService.AppendToolCallTurn(sessionId, AgentMessageRole.Assistant, "call-1", toolId, "{\"url\":\"https://example.com\"}");

        Assert.IsType<AgentToolInvocationRowViewModel>(Assert.Single(viewModel.Messages));
    }

    [Fact]
    public async Task AgentChatViewModel_ShowsActivityRowAgainAfterQuietPeriodWhileRunContinues()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var runRevision = runtime.SessionService.GetNextRunRevision(sessionId);
        runtime.SessionService.SaveCheckpoint(sessionId, runRevision, AgentRunStatus.Running, "Thinking.");
        using var viewModel = new AgentChatViewModel(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator,
            activityQuietDelay: TimeSpan.Zero);

        Assert.IsType<AgentActivityTranscriptRowViewModel>(Assert.Single(viewModel.Messages));

        runtime.SessionService.SaveCheckpoint(sessionId, runRevision, AgentRunStatus.Running, "Executing tool 'fetch_page'.");
        runtime.SessionService.AppendToolCallTurn(sessionId, AgentMessageRole.Assistant, "call-1", toolId, "{\"url\":\"https://example.com\"}");

        Assert.IsType<AgentToolInvocationRowViewModel>(viewModel.Messages[0]);
        var activityRow = Assert.IsType<AgentActivityTranscriptRowViewModel>(viewModel.Messages[^1]);
        Assert.StartsWith("Running Fetch Page", activityRow.ThinkingText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentChatViewModel_CompletionPreventsQuietActivityRowReturning()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var runRevision = runtime.SessionService.GetNextRunRevision(sessionId);
        runtime.SessionService.SaveCheckpoint(sessionId, runRevision, AgentRunStatus.Running, "Thinking.");
        using var viewModel = new AgentChatViewModel(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator,
            activityQuietDelay: TimeSpan.Zero);

        runtime.SessionService.AppendToolCallTurn(sessionId, AgentMessageRole.Assistant, "call-1", toolId, "{\"url\":\"https://example.com\"}");

        Assert.Contains(viewModel.Messages, row => row is AgentActivityTranscriptRowViewModel);

        runtime.SessionService.SaveCheckpoint(sessionId, runRevision, AgentRunStatus.Completed, "Done.");

        Assert.DoesNotContain(viewModel.Messages, row => row is AgentActivityTranscriptRowViewModel);
        Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
    }

    [Fact]
    public async Task AgentChatViewModel_NewUserTurnResetsActivityRowForNextRun()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        using var viewModel = new AgentChatViewModel(runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);

        var runRevision = runtime.SessionService.GetNextRunRevision(sessionId);
        runtime.SessionService.SaveCheckpoint(sessionId, runRevision, AgentRunStatus.Running, "Thinking.");
        runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.Assistant, "previous response");
        runtime.SessionService.SaveCheckpoint(sessionId, runRevision, AgentRunStatus.Completed, "Done.");

        Assert.DoesNotContain(viewModel.Messages, row => row is AgentActivityTranscriptRowViewModel);

        runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.User, "next request");
        runtime.SessionService.SaveCheckpoint(sessionId, runtime.SessionService.GetNextRunRevision(sessionId), AgentRunStatus.Running, "Thinking again.");

        Assert.IsType<AgentActivityTranscriptRowViewModel>(viewModel.Messages[^1]);
    }

    [Fact]
    public async Task BuildInstructionContextAsync_PassesRecentLiveBufferAndFormatsExplainableRecall()
    {
        const string toolId = "fetch_page";
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var session = runtime.SessionService.GetSession(sessionId)!;
        var profile = runtime.CurrentProfile;

        for (var index = 0; index < 10; index++)
        {
            runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.User, $"older-{index}");
        }

        var memoryFeature = new CapturingMemoryFeature
        {
            RecallResult = new AgentMemoryRecallResult(new[]
            {
                new AgentMemoryRecallEntry(
                    MemoryId: Guid.NewGuid().ToString("N"),
                    Category: "preference",
                    Content: "Prefer concise answers.",
                    EvidenceText: "User asked for short replies.",
                    Score: 12.5f,
                    IsPinned: true,
                    TrustState: AgentMemoryTrustState.Active,
                    SourceTurnId: Guid.NewGuid(),
                    MatchReasons: new[]
                    {
                        new AgentMemoryMatchReason("pinned", "Pinned memory is always considered for recall."),
                        new AgentMemoryMatchReason("token-overlap", "Shared 2 significant query term(s) with the current turn.")
                    })
            })
        };

        runtime.AddMemoryFeature(memoryFeature);

        var instructionContext = await runtime.MemoryCoordinator.BuildInstructionContextAsync(
            session,
            profile,
            Guid.NewGuid(),
            runRevision: 1,
            userMessage: "Please answer concisely.",
            runStartedAtUtc: DateTimeOffset.UtcNow,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(memoryFeature.LastRecallRequest);
        Assert.Equal(8, memoryFeature.LastRecallRequest!.RecentLiveBufferTurns.Count);
        Assert.Equal("older-2", RenderTurnText(memoryFeature.LastRecallRequest.RecentLiveBufferTurns[0]));
        Assert.Contains("[preference | Active] Prefer concise answers.", instructionContext.SystemInstructions, StringComparison.Ordinal);
        Assert.Contains("Why recalled: Pinned memory is always considered for recall.; Shared 2 significant query term(s) with the current turn.", instructionContext.SystemInstructions, StringComparison.Ordinal);
        Assert.Contains("Source turn:", instructionContext.SystemInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildInstructionContextAsync_SkipsRecallForSelfContainedRequest()
    {
        const string toolId = "fetch_page";
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var session = runtime.SessionService.GetSession(sessionId)!;
        var profile = runtime.CurrentProfile;
        var memoryFeature = new CapturingMemoryFeature();
        runtime.AddMemoryFeature(memoryFeature);

        var instructionContext = await runtime.MemoryCoordinator.BuildInstructionContextAsync(
            session,
            profile,
            Guid.NewGuid(),
            runRevision: 1,
            userMessage: "Create a simple hello world function in C#.",
            runStartedAtUtc: DateTimeOffset.UtcNow,
            cancellationToken: CancellationToken.None);

        Assert.Null(memoryFeature.LastRecallRequest);
        Assert.Equal(AgentMemoryRecallIntent.None, instructionContext.RecallPlan.Intent);
        Assert.Null(instructionContext.RecallResult);
    }

    [Fact]
    public async Task BuildInstructionContextAsync_BuildsPreferenceRecallPlan_ForStyleQuestion()
    {
        const string toolId = "fetch_page";
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var session = runtime.SessionService.GetSession(sessionId)!;
        var profile = runtime.CurrentProfile;
        var memoryFeature = new CapturingMemoryFeature();
        runtime.AddMemoryFeature(memoryFeature);

        var instructionContext = await runtime.MemoryCoordinator.BuildInstructionContextAsync(
            session,
            profile,
            Guid.NewGuid(),
            runRevision: 1,
            userMessage: "What response style do I prefer?",
            runStartedAtUtc: DateTimeOffset.UtcNow,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(memoryFeature.LastRecallRequest);
        Assert.Equal(AgentMemoryRecallIntent.Preference, memoryFeature.LastRecallRequest!.RecallPlan.Intent);
        Assert.Contains("preference", memoryFeature.LastRecallRequest.RecallPlan.PreferredCategories!);
        Assert.Contains("standing-instruction", memoryFeature.LastRecallRequest.RecallPlan.PreferredCategories!);
        Assert.Equal(AgentMemoryRecallIntent.Preference, instructionContext.RecallPlan.Intent);
    }

    [Fact]
    public async Task PublishLifecycleEventAsync_PassesRecentLiveBufferToMemoryFeatures()
    {
        const string toolId = "fetch_page";
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var session = runtime.SessionService.GetSession(sessionId)!;
        var profile = runtime.CurrentProfile;

        AgentTurnRecord? latestTurn = null;
        for (var index = 0; index < 9; index++)
        {
            latestTurn = runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.Assistant, $"turn-{index}");
        }

        var memoryFeature = new CapturingMemoryFeature();
        runtime.AddMemoryFeature(memoryFeature);

        await runtime.MemoryCoordinator.PublishLifecycleEventAsync(
            AgentLifecycleEventKind.AssistantTurnCompleted,
            session,
            profile,
            Guid.NewGuid(),
            runRevision: 2,
            status: AgentRunStatus.Completed,
            runStartedAtUtc: DateTimeOffset.UtcNow,
            userMessage: "Summarize the latest progress.",
            triggerTurn: latestTurn,
            checkpoint: new AgentRunCheckpointRecord(Guid.NewGuid(), sessionId, 2, AgentRunStatus.Completed, "done", DateTimeOffset.UtcNow),
            cancellationToken: CancellationToken.None);

        Assert.NotNull(memoryFeature.LastLifecycleEvent);
        Assert.Equal(8, memoryFeature.LastLifecycleEvent!.RecentLiveBufferTurns.Count);
        Assert.Equal("turn-1", RenderTurnText(memoryFeature.LastLifecycleEvent.RecentLiveBufferTurns[0]));
        Assert.Equal("turn-8", RenderTurnText(memoryFeature.LastLifecycleEvent.RecentLiveBufferTurns[^1]));
    }

    [Fact]
    public async Task MemorySemanticFeature_RecallAsync_ProvidesTrustStateSourceTurnAndMatchReasons()
    {
        var context = new TestPackageContext(Path.Combine(Path.GetTempPath(), "sunder-memory-tests", Guid.NewGuid().ToString("N")));
        var store = new MemoryLocalStore(context);
        var feature = CreateSemanticFeature(store);
        var sessionId = Guid.NewGuid();
        var sourceTurnId = Guid.NewGuid();

        store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            Category: "preference",
            Content: "Use concise responses for summaries.",
            NormalizedContent: "use concise responses for summaries.",
            EvidenceText: "The user asked for concise summaries.",
            SourceTurnId: sourceTurnId,
            IsPinned: true,
            Importance: 0.9f,
            Confidence: 0.95f));

        var userTurn = new AgentTurnRecord(
            Guid.NewGuid(),
            sessionId,
            AgentMessageRole.User,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(Guid.NewGuid(), Guid.NewGuid(), 0, AgentTurnItemKind.Text, "Please keep this summary concise.", null, null, null, null, null, null, false, false, null, null)],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var sessionContext = new AgentSessionContextRecord(sessionId, "profile", "Test Profile", "Test Session", AgentSessionState.Active, null);
        var runContext = new AgentRunContextRecord(Guid.NewGuid(), 1, AgentRunStatus.Running, IsInterrupted: false, DateTimeOffset.UtcNow);
        var turnContext = new AgentTurnContextRecord(sessionContext, runContext, "Please keep this summary concise.", null);

        var recall = await feature.RecallAsync(new AgentMemoryRecallRequest(
            sessionContext,
            runContext,
            turnContext,
            [userTurn],
            [userTurn],
            new AgentMemoryRecallPlan(
                AgentMemoryRecallIntent.Preference,
                "Please keep this summary concise.",
                PreferredCategories: ["preference", "standing-instruction"],
                MaxEntryCount: 4,
                MaxChars: 1200)));

        var entry = Assert.Single(recall!.Entries);
        Assert.Equal(AgentMemoryTrustState.Active, entry.TrustState);
        Assert.Equal(sourceTurnId, entry.SourceTurnId);
        Assert.Contains(entry.MatchReasons!, reason => reason.Kind == "pinned");
        Assert.Contains(entry.MatchReasons!, reason => reason.Kind == "always-include-category");
    }

    [Fact]
    public async Task MemorySemanticFeature_HandleLifecycleEventAsync_PromotesToolResultProjectFacts()
    {
        var context = new TestPackageContext(Path.Combine(Path.GetTempPath(), "sunder-memory-tests", Guid.NewGuid().ToString("N")));
        var store = new MemoryLocalStore(context);
        var feature = CreateSemanticFeature(store);
        var sessionId = Guid.NewGuid();
        var toolTurnId = Guid.NewGuid();
        var toolTurn = new AgentTurnRecord(
            toolTurnId,
            sessionId,
            AgentMessageRole.Tool,
            AgentTurnKind.ToolResult,
            [new AgentTurnItemRecord(
                Guid.NewGuid(),
                toolTurnId,
                0,
                AgentTurnItemKind.ToolResult,
                "TargetFramework: net10.0\nProject uses ASP.NET Core and Blazor Server.",
                "call-1",
                "inspect_project",
                null,
                "Detected project stack.",
                null,
                null,
                false,
                false,
                null,
                null)],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        await feature.HandleLifecycleEventAsync(new AgentLifecycleEvent(
            AgentLifecycleEventKind.ToolResultRecorded,
            new AgentSessionContextRecord(sessionId, "profile", "Test Profile", "Test Session", AgentSessionState.Active, null),
            new AgentRunContextRecord(Guid.NewGuid(), 1, AgentRunStatus.Running, IsInterrupted: false, DateTimeOffset.UtcNow),
            new AgentTurnContextRecord(new AgentSessionContextRecord(sessionId, "profile", "Test Profile", "Test Session", AgentSessionState.Active, null), new AgentRunContextRecord(Guid.NewGuid(), 1, AgentRunStatus.Running, IsInterrupted: false, DateTimeOffset.UtcNow), "Inspect the project stack.", null),
            [toolTurn],
            [toolTurn],
            TriggerTurn: toolTurn));

        var promoted = store.ListMemories(sessionId);
        Assert.Contains(promoted, memory => memory.Category == "project-fact" && memory.Content.Contains("TargetFramework: net10.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MemorySemanticFeature_HandleLifecycleEventAsync_MergesNearDuplicateUserMemories()
    {
        var context = new TestPackageContext(Path.Combine(Path.GetTempPath(), "sunder-memory-tests", Guid.NewGuid().ToString("N")));
        var store = new MemoryLocalStore(context);
        var feature = CreateSemanticFeature(store);
        var sessionId = Guid.NewGuid();

        await feature.HandleLifecycleEventAsync(BuildUserLifecycleEvent(sessionId, "I prefer concise summary responses for status updates."));
        await feature.HandleLifecycleEventAsync(BuildUserLifecycleEvent(sessionId, "I prefer concise responses for status update summaries."));

        var promoted = store.ListMemories(sessionId);
        Assert.Single(promoted, item => item.Category == "preference");
        var memory = promoted.Single(item => item.Category == "preference");
        Assert.Contains("concise", memory.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MemorySemanticFeature_RecallAsync_UsesProfileSelectedEmbeddingProviderForSemanticRecall()
    {
        const string toolId = "fetch_page";
        const string embeddingProviderId = "test-embeddings";
        const string embeddingModelId = "semantic-v1";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        runtime.AddEmbeddingProvider(new TestEmbeddingProvider(embeddingProviderId));

        var sessionId = await runtime.CreateSessionAsync(toolId);
        var session = runtime.SessionService.GetSession(sessionId)!;
        var profile = runtime.CurrentProfile;
        runtime.ProfileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            profile.ChatProviderId,
            profile.ChatModelId,
            embeddingProviderId,
            embeddingModelId,
            selectableCapabilityAssignments: profile.SelectableCapabilityAssignments);

        var updatedProfile = runtime.ProfileService.GetProfile(profile.ProfileId)!;
        var context = new TestPackageContext(Path.Combine(Path.GetTempPath(), "sunder-memory-tests", Guid.NewGuid().ToString("N")));
        var store = new MemoryLocalStore(context);
        var feature = CreateSemanticFeature(store, runtime.ExtensionCatalog);

        store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            Category: "remembered-fact",
            Content: "The preferred summary style is concise and direct.",
            NormalizedContent: "the preferred summary style is concise and direct.",
            EvidenceText: "Stored style preference.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: false,
            Importance: 0.8f,
            Confidence: 0.85f));

        var sessionContext = new AgentSessionContextRecord(sessionId, updatedProfile.ProfileId, updatedProfile.DisplayName, session.Title, session.State, null);
        var runContext = new AgentRunContextRecord(Guid.NewGuid(), 1, AgentRunStatus.Running, IsInterrupted: false, DateTimeOffset.UtcNow);
        var turnContext = new AgentTurnContextRecord(sessionContext, runContext, "Please keep this brief.", null);
        var userTurn = new AgentTurnRecord(
            Guid.NewGuid(),
            sessionId,
            AgentMessageRole.User,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(Guid.NewGuid(), Guid.NewGuid(), 0, AgentTurnItemKind.Text, "Please keep this brief.", null, null, null, null, null, null, false, false, null, null)],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var recall = await feature.RecallAsync(new AgentMemoryRecallRequest(
            sessionContext,
            runContext,
            turnContext,
            [userTurn],
            [userTurn],
            new AgentMemoryRecallPlan(
                AgentMemoryRecallIntent.GeneralFact,
                "Please keep this brief.",
                PreferredCategories: ["remembered-fact", "preference"],
                MaxEntryCount: 6,
                MaxChars: 1800)));

        var entry = Assert.Single(recall!.Entries);
        Assert.Contains(entry.MatchReasons!, reason => reason.Kind == "semantic-similarity");
    }

    [Fact]
    public async Task MemorySemanticFeature_RecallAsync_RespectsPreferredCategories()
    {
        var context = new TestPackageContext(Path.Combine(Path.GetTempPath(), "sunder-memory-tests", Guid.NewGuid().ToString("N")));
        var store = new MemoryLocalStore(context);
        var feature = CreateSemanticFeature(store);
        var sessionId = Guid.NewGuid();

        store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            Category: "preference",
            Content: "Prefer concise summaries.",
            NormalizedContent: "prefer concise summaries.",
            EvidenceText: "Preference evidence.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: false,
            Importance: 0.8f,
            Confidence: 0.85f));
        store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            Category: "project-fact",
            Content: "This project uses Blazor Server.",
            NormalizedContent: "this project uses blazor server.",
            EvidenceText: "Project evidence.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: false,
            Importance: 0.8f,
            Confidence: 0.85f));

        var sessionContext = new AgentSessionContextRecord(sessionId, "profile", "Test Profile", "Test Session", AgentSessionState.Active, null);
        var runContext = new AgentRunContextRecord(Guid.NewGuid(), 1, AgentRunStatus.Running, IsInterrupted: false, DateTimeOffset.UtcNow);
        var turnContext = new AgentTurnContextRecord(sessionContext, runContext, "What response style do I prefer?", null);
        var userTurn = new AgentTurnRecord(
            Guid.NewGuid(),
            sessionId,
            AgentMessageRole.User,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(Guid.NewGuid(), Guid.NewGuid(), 0, AgentTurnItemKind.Text, "What response style do I prefer?", null, null, null, null, null, null, false, false, null, null)],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var recall = await feature.RecallAsync(new AgentMemoryRecallRequest(
            sessionContext,
            runContext,
            turnContext,
            [userTurn],
            [userTurn],
            new AgentMemoryRecallPlan(
                AgentMemoryRecallIntent.Preference,
                "What response style do I prefer?",
                PreferredCategories: ["preference"],
                MaxEntryCount: 4,
                MaxChars: 1200)));

        var entry = Assert.Single(recall!.Entries);
        Assert.Equal("preference", entry.Category);
    }

    [Fact]
    public void MemoryLocalStore_SearchMemories_UsesFullTextIndex()
    {
        var context = new TestPackageContext(Path.Combine(Path.GetTempPath(), "sunder-memory-tests", Guid.NewGuid().ToString("N")));
        var store = new MemoryLocalStore(context);
        var sessionId = Guid.NewGuid();

        store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            Category: "project-fact",
            Content: "This project uses Blazor Server and ASP.NET Core.",
            NormalizedContent: "this project uses blazor server and asp.net core.",
            EvidenceText: "Project stack evidence.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: false,
            Importance: 0.8f,
            Confidence: 0.85f));
        store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            Category: "environment-fact",
            Content: "The working directory is /workspace.",
            NormalizedContent: "the working directory is /workspace.",
            EvidenceText: "Environment evidence.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: false,
            Importance: 0.8f,
            Confidence: 0.85f));

        var results = store.SearchMemories(sessionId, "Blazor", preferredCategories: ["project-fact"], includeInactive: false, limit: 10);

        var result = Assert.Single(results);
        Assert.Equal("project-fact", result.Memory.Category);
        Assert.Contains("Blazor", result.Memory.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemorySemanticFeature_RecallAsync_ReportsFullTextMatchReason()
    {
        var context = new TestPackageContext(Path.Combine(Path.GetTempPath(), "sunder-memory-tests", Guid.NewGuid().ToString("N")));
        var store = new MemoryLocalStore(context);
        var feature = CreateSemanticFeature(store);
        var sessionId = Guid.NewGuid();

        store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            Category: "project-fact",
            Content: "This project uses Blazor Server and ASP.NET Core.",
            NormalizedContent: "this project uses blazor server and asp.net core.",
            EvidenceText: "Project stack evidence.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: false,
            Importance: 0.8f,
            Confidence: 0.85f));

        var sessionContext = new AgentSessionContextRecord(sessionId, "profile", "Test Profile", "Test Session", AgentSessionState.Active, null);
        var runContext = new AgentRunContextRecord(Guid.NewGuid(), 1, AgentRunStatus.Running, IsInterrupted: false, DateTimeOffset.UtcNow);
        var turnContext = new AgentTurnContextRecord(sessionContext, runContext, "What framework does this project use?", null);
        var userTurn = new AgentTurnRecord(
            Guid.NewGuid(),
            sessionId,
            AgentMessageRole.User,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(Guid.NewGuid(), Guid.NewGuid(), 0, AgentTurnItemKind.Text, "What framework does this project use?", null, null, null, null, null, null, false, false, null, null)],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var recall = await feature.RecallAsync(new AgentMemoryRecallRequest(
            sessionContext,
            runContext,
            turnContext,
            [userTurn],
            [userTurn],
            new AgentMemoryRecallPlan(
                AgentMemoryRecallIntent.ProjectFact,
                "What framework does this project use?",
                PreferredCategories: ["project-fact"],
                MaxEntryCount: 4,
                MaxChars: 1200)));

        var entry = Assert.Single(recall!.Entries);
        Assert.Contains(entry.MatchReasons!, reason => reason.Kind == "full-text-match");
    }

    [Fact]
    public async Task MemorySemanticFeature_RecallAsync_ProjectsContestedTrustState()
    {
        var context = new TestPackageContext(Path.Combine(Path.GetTempPath(), "sunder-memory-tests", Guid.NewGuid().ToString("N")));
        var store = new MemoryLocalStore(context);
        var feature = CreateSemanticFeature(store);
        var sessionId = Guid.NewGuid();

        var memory = store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            Category: "preference",
            Content: "Prefer concise summaries.",
            NormalizedContent: "prefer concise summaries.",
            EvidenceText: "Preference evidence.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: true,
            Importance: 0.8f,
            Confidence: 0.85f));
        store.SetContested(memory.MemoryId);

        var sessionContext = new AgentSessionContextRecord(sessionId, "profile", "Test Profile", "Test Session", AgentSessionState.Active, null);
        var runContext = new AgentRunContextRecord(Guid.NewGuid(), 1, AgentRunStatus.Running, IsInterrupted: false, DateTimeOffset.UtcNow);
        var turnContext = new AgentTurnContextRecord(sessionContext, runContext, "What response style do I prefer?", null);
        var userTurn = new AgentTurnRecord(
            Guid.NewGuid(),
            sessionId,
            AgentMessageRole.User,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(Guid.NewGuid(), Guid.NewGuid(), 0, AgentTurnItemKind.Text, "What response style do I prefer?", null, null, null, null, null, null, false, false, null, null)],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var recall = await feature.RecallAsync(new AgentMemoryRecallRequest(
            sessionContext,
            runContext,
            turnContext,
            [userTurn],
            [userTurn],
            new AgentMemoryRecallPlan(
                AgentMemoryRecallIntent.Preference,
                "What response style do I prefer?",
                PreferredCategories: ["preference"],
                MaxEntryCount: 4,
                MaxChars: 1200)));

        var entry = Assert.Single(recall!.Entries);
        Assert.Equal(AgentMemoryTrustState.Contested, entry.TrustState);
        Assert.Contains(entry.MatchReasons!, reason => reason.Kind == "contested");
    }

    [Fact]
    public void MemoryInspectorService_CreateCorrectedMemory_LinksSupersession()
    {
        var context = new TestPackageContext(Path.Combine(Path.GetTempPath(), "sunder-memory-tests", Guid.NewGuid().ToString("N")));
        var store = new MemoryLocalStore(context);
        var settings = new MemorySemanticSettingsService(context);
        var extensionCatalog = new TestExtensionCatalog();
        var metrics = new SemanticMemoryMetricsService();
        var retrievalBackend = new SemanticMemoryRetrievalBackend(store, new ProfileConfiguredEmbeddingProviderResolver(extensionCatalog), settings);
        var indexingBackgroundService = new SemanticMemoryIndexingBackgroundService(store, extensionCatalog, settings, retrievalBackend, metrics);
        var inspector = new MemoryInspectorService(
            extensionCatalog,
            store,
            retrievalBackend,
            settings,
            indexingBackgroundService,
            new SemanticEmbeddingContextResolver(extensionCatalog, settings),
            metrics);
        var sessionId = Guid.NewGuid();

        var source = store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            Category: "project-fact",
            Content: "The project uses old framework wording.",
            NormalizedContent: "the project uses old framework wording.",
            EvidenceText: "Original evidence.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: false,
            Importance: 0.8f,
            Confidence: 0.85f));

        var correction = inspector.CreateCorrectedMemory(source.MemoryId, "project-fact", "The project uses updated framework wording.");
        var updatedSource = inspector.GetMemory(source.MemoryId)!;
        var superseding = inspector.GetSupersedingMemory(source.MemoryId)!;
        var supersededList = inspector.ListSupersededMemories(superseding.MemoryId);

        Assert.Equal(MemoryLocalStore.SupersededState, updatedSource.State);
        Assert.Equal(superseding.MemoryId, updatedSource.SupersededByMemoryId);
        Assert.Equal(correction.CorrectedMemory.MemoryId, superseding.MemoryId);
        Assert.Contains(supersededList, item => item.MemoryId == source.MemoryId);
        Assert.True(inspector.ListCorrectionLineage(source.MemoryId).Count >= 1);
    }

    [Fact]
    public async Task MemoryInspectorService_GetSemanticIndexStatus_ReportsIndexedState()
    {
        const string toolId = "fetch_page";
        const string embeddingProviderId = "test-embeddings";
        const string embeddingModelId = "semantic-v1";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        runtime.AddEmbeddingProvider(new TestEmbeddingProvider(embeddingProviderId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var session = runtime.SessionService.GetSession(sessionId)!;
        var profile = runtime.CurrentProfile;
        runtime.ProfileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            profile.ChatProviderId,
            profile.ChatModelId,
            embeddingProviderId,
            embeddingModelId,
            selectableCapabilityAssignments: profile.SelectableCapabilityAssignments);

        var context = new TestPackageContext(Path.Combine(Path.GetTempPath(), "sunder-memory-tests", Guid.NewGuid().ToString("N")));
        var store = new MemoryLocalStore(context);
        var settings = new MemorySemanticSettingsService(context);
        var metrics = new SemanticMemoryMetricsService();
        var retrievalBackend = new SemanticMemoryRetrievalBackend(store, new ProfileConfiguredEmbeddingProviderResolver(runtime.ExtensionCatalog), settings);
        var indexingBackgroundService = new SemanticMemoryIndexingBackgroundService(store, runtime.ExtensionCatalog, settings, retrievalBackend, metrics);
        var inspector = new MemoryInspectorService(
            runtime.ExtensionCatalog,
            store,
            retrievalBackend,
            settings,
            indexingBackgroundService,
            new SemanticEmbeddingContextResolver(runtime.ExtensionCatalog, settings),
            metrics);
        var memory = store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            Category: "remembered-fact",
            Content: "Prefer concise summaries.",
            NormalizedContent: "prefer concise summaries.",
            EvidenceText: "Stored style preference.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: false,
            Importance: 0.8f,
            Confidence: 0.85f));

        await inspector.ReindexSessionAsync(sessionId, profile.ProfileId);
        var semanticState = await inspector.GetSemanticSessionStateAsync(sessionId, profile.ProfileId);
        var indexStatus = inspector.GetSemanticIndexStatus(memory, semanticState.Context);

        Assert.Equal("Indexed", indexStatus.StatusLabel);
        Assert.Equal(SemanticMemoryEntryIndexState.Indexed, indexStatus.IndexState);
        Assert.Contains(embeddingModelId, indexStatus.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryInspectorService_ReindexSessionAsync_IndexesCurrentProfileSelection()
    {
        const string toolId = "fetch_page";
        const string embeddingProviderId = "test-embeddings";
        const string embeddingModelId = "semantic-v1";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        runtime.AddEmbeddingProvider(new TestEmbeddingProvider(embeddingProviderId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var session = runtime.SessionService.GetSession(sessionId)!;
        var profile = runtime.CurrentProfile;
        runtime.ProfileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            profile.ChatProviderId,
            profile.ChatModelId,
            embeddingProviderId,
            embeddingModelId,
            selectableCapabilityAssignments: profile.SelectableCapabilityAssignments);

        var context = new TestPackageContext(Path.Combine(Path.GetTempPath(), "sunder-memory-tests", Guid.NewGuid().ToString("N")));
        var store = new MemoryLocalStore(context);
        store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            Category: "remembered-fact",
            Content: "Prefer concise summaries.",
            NormalizedContent: "prefer concise summaries.",
            EvidenceText: "Stored style preference.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: false,
            Importance: 0.8f,
            Confidence: 0.85f));

        var settings = new MemorySemanticSettingsService(context);
        var metrics = new SemanticMemoryMetricsService();
        var retrievalBackend = new SemanticMemoryRetrievalBackend(store, new ProfileConfiguredEmbeddingProviderResolver(runtime.ExtensionCatalog), settings);
        var indexingBackgroundService = new SemanticMemoryIndexingBackgroundService(store, runtime.ExtensionCatalog, settings, retrievalBackend, metrics);
        var inspector = new MemoryInspectorService(
            runtime.ExtensionCatalog,
            store,
            retrievalBackend,
            settings,
            indexingBackgroundService,
            new SemanticEmbeddingContextResolver(runtime.ExtensionCatalog, settings),
            metrics);

        var result = await inspector.ReindexSessionAsync(sessionId, profile.ProfileId);
        var status = await inspector.GetSemanticStatusAsync(sessionId, profile.ProfileId);

        Assert.Equal(1, result.IndexedMemoryCount);
        Assert.Contains("Indexed memories: 1", status!.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryInspectorService_GetSemanticStatusAsync_ReportsDisabledWhenSettingOff()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var context = new TestPackageContext(
            Path.Combine(Path.GetTempPath(), "sunder-memory-tests", Guid.NewGuid().ToString("N")),
            configurationValues: new Dictionary<string, string> { ["semantic.enabled"] = "false" });
        var store = new MemoryLocalStore(context);
        var settings = new MemorySemanticSettingsService(context);
        var metrics = new SemanticMemoryMetricsService();
        var retrievalBackend = new SemanticMemoryRetrievalBackend(store, new ProfileConfiguredEmbeddingProviderResolver(runtime.ExtensionCatalog), settings);
        var indexingBackgroundService = new SemanticMemoryIndexingBackgroundService(store, runtime.ExtensionCatalog, settings, retrievalBackend, metrics);
        var inspector = new MemoryInspectorService(
            runtime.ExtensionCatalog,
            store,
            retrievalBackend,
            settings,
            indexingBackgroundService,
            new SemanticEmbeddingContextResolver(runtime.ExtensionCatalog, settings),
            metrics);

        var status = await inspector.GetSemanticStatusAsync(sessionId);

        Assert.NotNull(status);
        Assert.False(status!.CanReindex);
        Assert.Contains("disabled", status.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SemanticMemoryIndexingBackgroundService_IndexesQueuedMemory()
    {
        const string toolId = "fetch_page";
        const string embeddingProviderId = "test-embeddings";
        const string embeddingModelId = "semantic-v1";

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")), new TestTool(toolId));
        runtime.AddEmbeddingProvider(new TestEmbeddingProvider(embeddingProviderId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var session = runtime.SessionService.GetSession(sessionId)!;
        var profile = runtime.CurrentProfile;
        runtime.ProfileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            profile.ChatProviderId,
            profile.ChatModelId,
            embeddingProviderId,
            embeddingModelId,
            selectableCapabilityAssignments: profile.SelectableCapabilityAssignments);

        var context = new TestPackageContext(Path.Combine(Path.GetTempPath(), "sunder-memory-tests", Guid.NewGuid().ToString("N")));
        var store = new MemoryLocalStore(context);
        var settings = new MemorySemanticSettingsService(context);
        var retrievalBackend = new SemanticMemoryRetrievalBackend(store, new ProfileConfiguredEmbeddingProviderResolver(runtime.ExtensionCatalog), settings);
        var metrics = new SemanticMemoryMetricsService();
        var backgroundService = new SemanticMemoryIndexingBackgroundService(store, runtime.ExtensionCatalog, settings, retrievalBackend, metrics);

        var memory = store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            Category: "remembered-fact",
            Content: "Please keep answers brief.",
            NormalizedContent: "please keep answers brief.",
            EvidenceText: "Stored style preference.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: false,
            Importance: 0.8f,
            Confidence: 0.85f));

        await backgroundService.StartAsync();
        Assert.True(backgroundService.QueueMemoryIndex(memory.MemoryId, profile.ProfileId));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (store.GetEmbedding(memory.MemoryId) is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        await backgroundService.StopAsync();

        var embedding = store.GetEmbedding(memory.MemoryId);
        Assert.NotNull(embedding);
        Assert.Equal(embeddingProviderId, embedding!.ProviderId);
        Assert.Equal(embeddingModelId, embedding.ModelId);
    }

    [Fact]
    public async Task SemanticMemoryIndexingBackgroundService_ReportsFailureStatus()
    {
        const string embeddingProviderId = "failing-embeddings";
        var extensionCatalog = new TestExtensionCatalog();
        extensionCatalog.AddExtension(PackageExtensionPoints.EmbeddingProviders, new ThrowingEmbeddingProvider(embeddingProviderId));

        var runtimeCatalog = new TestRuntimeCatalog(
            [new AgentProfileRecord(
                "profile-1",
                "Test Profile",
                null,
                null,
                null,
                null,
                embeddingProviderId,
                "semantic-v1",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                [])],
            [new AgentSessionRecord(Guid.NewGuid(), "Test Session", AgentSessionState.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)],
            [new AgentWorkspaceRecord("workspace-1", "Workspace", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        extensionCatalog.AddExtension(PackageExtensionPoints.RuntimeCatalogs, runtimeCatalog);

        var context = new TestPackageContext(Path.Combine(Path.GetTempPath(), "sunder-memory-tests", Guid.NewGuid().ToString("N")));
        var store = new MemoryLocalStore(context);
        var settings = new MemorySemanticSettingsService(context);
        var retrievalBackend = new SemanticMemoryRetrievalBackend(store, new ProfileConfiguredEmbeddingProviderResolver(extensionCatalog), settings);
        var metrics = new SemanticMemoryMetricsService();
        var backgroundService = new SemanticMemoryIndexingBackgroundService(store, extensionCatalog, settings, retrievalBackend, metrics);
        var memory = store.UpsertMemory(new MemoryUpsertRequest(
            runtimeCatalog.Session.SessionId,
            Category: "remembered-fact",
            Content: "Prefer concise summaries.",
            NormalizedContent: "prefer concise summaries.",
            EvidenceText: "Stored style preference.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: false,
            Importance: 0.8f,
            Confidence: 0.85f));

        await backgroundService.StartAsync();
        Assert.True(backgroundService.QueueMemoryIndex(memory.MemoryId, "profile-1"));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        SemanticMemoryIndexWorkerStatus status;
        do
        {
            await Task.Delay(50);
            status = backgroundService.GetStatus();
        }
        while (string.IsNullOrWhiteSpace(status.LastFailureMessage) && DateTime.UtcNow < deadline);

        await backgroundService.StopAsync();

        Assert.NotNull(status.LastFailureAtUtc);
        Assert.Contains("Embedding generation failed", status.LastFailureMessage, StringComparison.Ordinal);
        Assert.Equal(0, status.PendingItemCount);
    }

    private static AgentLifecycleEvent BuildUserLifecycleEvent(Guid sessionId, string text)
    {
        var turnId = Guid.NewGuid();
        var userTurn = new AgentTurnRecord(
            turnId,
            sessionId,
            AgentMessageRole.User,
            AgentTurnKind.Message,
            [new AgentTurnItemRecord(Guid.NewGuid(), turnId, 0, AgentTurnItemKind.Text, text, null, null, null, null, null, null, false, false, null, null)],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var sessionContext = new AgentSessionContextRecord(sessionId, "profile", "Test Profile", "Test Session", AgentSessionState.Active, null);
        var runContext = new AgentRunContextRecord(Guid.NewGuid(), 1, AgentRunStatus.Running, IsInterrupted: false, DateTimeOffset.UtcNow);
        return new AgentLifecycleEvent(
            AgentLifecycleEventKind.UserTurnAdded,
            sessionContext,
            runContext,
            new AgentTurnContextRecord(sessionContext, runContext, text, null),
            [userTurn],
            [userTurn],
            TriggerTurn: userTurn);
    }

    private static AgentSystemPromptRequest BuildSystemPromptRequest(
        IReadOnlyList<AgentToolDescriptor>? availableTools = null,
        AgentWorkspaceRecord? workspace = null,
        AgentWorkspaceBindingRecord? executionBinding = null)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new AgentSessionRecord(Guid.NewGuid(), "Test Session", AgentSessionState.Active, now, now);
        var profile = new AgentProfileRecord(
            "profile",
            "Test Profile",
            null,
            null,
            "test-provider",
            "test-model",
            null,
            null,
            now,
            now,
            [],
            []);

        return new AgentSystemPromptRequest(
            session,
            profile,
            "test-provider",
            "test-model",
            new AgentProviderRunCapabilities(
                SupportsNativeToolCalling: true,
                SupportsStreamingToolCalls: true,
                SupportsMultipleToolCalls: false,
                Summary: "Test provider supports native tool calling."),
            workspace,
            executionBinding,
            availableTools ?? [],
            [],
            Guid.NewGuid(),
            RunRevision: 1,
            now,
            "Test user message.");
    }

    private static MemorySemanticFeature CreateSemanticFeature(MemoryLocalStore store, TestExtensionCatalog? extensionCatalog = null)
    {
        var catalog = extensionCatalog ?? new TestExtensionCatalog();
        var settingsContext = new TestPackageContext(Path.Combine(Path.GetTempPath(), "sunder-memory-settings", Guid.NewGuid().ToString("N")));
        var settings = new MemorySemanticSettingsService(settingsContext);
        var metrics = new SemanticMemoryMetricsService();
        var retrievalBackend = new SemanticMemoryRetrievalBackend(
            store,
            new ProfileConfiguredEmbeddingProviderResolver(catalog),
            settings);
        var indexingBackgroundService = new SemanticMemoryIndexingBackgroundService(store, catalog, settings, retrievalBackend, metrics);
        return new MemorySemanticFeature(
            store,
            new SemanticMemoryRecallService(store, retrievalBackend, metrics),
            new SemanticMemoryPromotionService(store, indexingBackgroundService, metrics),
            new MemoryWorkingSummaryBuilder(store));
    }

    private static StoredMemoryRecord StoreMemoryWithEmbedding(
        MemoryLocalStore store,
        Guid sessionId,
        string providerId,
        string modelId,
        string content)
    {
        var memory = store.UpsertMemory(new MemoryUpsertRequest(
            sessionId,
            Category: "remembered-fact",
            Content: content,
            NormalizedContent: content.ToLowerInvariant(),
            EvidenceText: $"Evidence for {content}.",
            SourceTurnId: Guid.NewGuid(),
            IsPinned: false,
            Importance: 0.8f,
            Confidence: 0.85f));
        var now = DateTimeOffset.UtcNow;
        store.UpsertEmbedding(new StoredMemoryEmbeddingRecord(
            memory.MemoryId,
            sessionId,
            providerId,
            modelId,
            $"hash-{memory.MemoryId:N}",
            2,
            [0.1f, 0.2f],
            now,
            now));
        return memory;
    }

    private sealed record AgentProviderRequest(
        string ProviderId,
        string ModelId,
        string? SystemInstructions,
        IReadOnlyList<AgentTurnRecord> Turns,
        IReadOnlyList<AgentToolDescriptor>? AvailableTools = null,
        ReasoningEffort? ReasoningEffort = null);

    private sealed record AgentProviderStreamEvent(
        AgentProviderStreamEventType Type,
        string? Delta = null,
        AgentProviderResponse? Response = null,
        AgentToolCallRequest? ToolCall = null,
        IReadOnlyList<AgentToolCallRequest>? ToolCalls = null);

    private enum AgentProviderStreamEventType
    {
        TextDelta = 0,
        Completed = 1,
        Error = 2,
        ToolCallRequested = 3,
    }

    private sealed record AgentProviderResponse(
        string Content,
        bool IsError = false,
        string? ErrorCode = null);

    private static AgentProviderStreamEvent AssertAndComplete(AgentProviderRequest request, string toolId, string callId)
    {
        Assert.Contains(request.Turns, turn =>
            turn.Kind == AgentTurnKind.ToolResult
            && turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolResult
                                      && item.ToolId == toolId
                                      && item.CallId == callId));
        AssertNoOrphanToolResults(request);

        return Complete("Used the tool result without refetching.");
    }

    private static void AssertNoOrphanToolResults(AgentProviderRequest request)
    {
        var callIds = request.Turns
            .SelectMany(turn => turn.Items)
            .Where(item => item.Kind == AgentTurnItemKind.ToolCall && !string.IsNullOrWhiteSpace(item.CallId))
            .Select(item => item.CallId!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var result in request.Turns.SelectMany(turn => turn.Items).Where(item => item.Kind == AgentTurnItemKind.ToolResult))
        {
            Assert.True(!string.IsNullOrWhiteSpace(result.CallId) && callIds.Contains(result.CallId!), $"Tool result '{result.CallId}' was sent without its matching tool call.");
        }
    }

    private static void AssertNoUnpairedToolItems(AgentProviderRequest request)
    {
        var items = request.Turns.SelectMany(turn => turn.Items).ToArray();
        var callIds = items
            .Where(item => item.Kind == AgentTurnItemKind.ToolCall && !string.IsNullOrWhiteSpace(item.CallId))
            .Select(item => item.CallId!)
            .ToHashSet(StringComparer.Ordinal);
        var resultIds = items
            .Where(item => item.Kind == AgentTurnItemKind.ToolResult && !string.IsNullOrWhiteSpace(item.CallId))
            .Select(item => item.CallId!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var callId in callIds)
        {
            Assert.True(resultIds.Contains(callId), $"Tool call '{callId}' was sent without its matching tool result.");
        }

        foreach (var resultId in resultIds)
        {
            Assert.True(callIds.Contains(resultId), $"Tool result '{resultId}' was sent without its matching tool call.");
        }
    }

    private static AgentProviderStreamEvent AssertDeniedToolResultAndComplete(AgentProviderRequest request, string toolId, string callId)
    {
        Assert.Contains(request.Turns, turn =>
            turn.Kind == AgentTurnKind.ToolResult
            && turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolResult
                                      && item.ToolId == toolId
                                      && item.CallId == callId
                                      && item.TextContent is not null
                                      && item.TextContent.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)));
        AssertNoUnpairedToolItems(request);

        return Complete("A calm poem after the denied tool call.");
    }

    private static AgentProviderStreamEvent AssertErroredToolResultAndComplete(
        AgentProviderRequest request,
        string toolId,
        string callId,
        string errorCode,
        string expectedContent = "fatal: not a git repository")
    {
        Assert.Contains(request.Turns, turn =>
            turn.Kind == AgentTurnKind.ToolResult
            && turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolResult
                                      && item.ToolId == toolId
                                      && item.CallId == callId
                                      && item.TextContent is not null
                                      && item.TextContent.Contains(expectedContent, StringComparison.Ordinal)));
        AssertNoUnpairedToolItems(request);

        return Complete("Interpreted the tool error and continued.");
    }

    private static AgentProviderStreamEvent AssertDuplicateReuseAndComplete(AgentProviderRequest request, string toolId)
    {
        Assert.Contains(request.Turns, turn =>
            turn.Kind == AgentTurnKind.ToolResult
            && turn.Items.Any(item => item.Kind == AgentTurnItemKind.ToolResult
                                      && item.ToolId == toolId
                                      && item.TextContent is not null
                                      && item.TextContent.Contains("Duplicate read-only tool call skipped", StringComparison.Ordinal)));

        return Complete("Finished after reusing the cached read-only result.");
    }

    private static void AssertActiveExchange(AgentProviderRequest request, int requestIndex, string currentUserMessage)
    {
        var activeStartIndex = request.Turns
            .Select((turn, index) => new { turn, index })
            .FirstOrDefault(entry => entry.turn.Role == AgentMessageRole.User
                                     && entry.turn.Kind == AgentTurnKind.Message
                                     && RenderTurnText(entry.turn) == currentUserMessage)
            ?.index ?? -1;

        Assert.True(activeStartIndex >= 0, "The current run's user turn should always remain in the provider request.");

        var activeTurns = request.Turns.Skip(activeStartIndex).ToArray();
        var expectedActiveTurnCount = 1 + ((requestIndex - 1) * 2);

        Assert.Equal(expectedActiveTurnCount, activeTurns.Length);
        Assert.Equal(currentUserMessage, RenderTurnText(activeTurns[0]));
    }

    private static AgentProviderStreamEvent AssertToolAvailableAndRequest(
        AgentProviderRequest request,
        string toolId,
        string callId,
        string argumentsJson)
    {
        Assert.Contains(request.AvailableTools ?? [], tool => string.Equals(tool.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
        return ToolRequest(callId, toolId, argumentsJson);
    }

    private static AgentProviderStreamEvent ToolRequest(string callId, string toolId, string argumentsJson)
        => new(
            AgentProviderStreamEventType.ToolCallRequested,
            ToolCall: new AgentToolCallRequest(callId, toolId, argumentsJson));

    private static AgentProviderStreamEvent ToolRequests(params AgentToolCallRequest[] toolCalls)
        => new(AgentProviderStreamEventType.ToolCallRequested, ToolCalls: toolCalls);

    private static AgentProviderStreamEvent Delta(string delta)
        => new(AgentProviderStreamEventType.TextDelta, Delta: delta);

    private static AgentProviderStreamEvent Complete(string content)
        => new(
            AgentProviderStreamEventType.Completed,
            Response: new AgentProviderResponse(content));

    private static AgentProviderStreamEvent TransientStreamError()
        => new(
            AgentProviderStreamEventType.Error,
            Response: new AgentProviderResponse(
                "The response ended prematurely. (ResponseEnded)",
                IsError: true,
                ErrorCode: "ResponseEnded"));

    private static string RenderTurnText(AgentTurnRecord turn)
        => string.Join("\n\n", turn.Items
            .Where(item => item.Kind == AgentTurnItemKind.Text && !string.IsNullOrWhiteSpace(item.TextContent))
            .Select(item => item.TextContent!.Trim()));

    private static ConfiguredMcpServerRecord CreateMcpServer(string serverId, string name, string displayName)
        => new()
        {
            ServerId = serverId,
            Name = name,
            DisplayName = displayName,
            Description = displayName + " tools.",
            IsEnabled = true,
            TransportType = ConfiguredMcpTransportType.Stdio,
            CommandParts = ["node", "server.js"],
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
        while (!condition())
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeoutCts.Token);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Test cleanup should not hide assertion failures.
        }
    }

    private sealed class AgentTestRuntime : IDisposable
    {
        private readonly string _rootPath;
        private readonly TestExtensionCatalog _extensionCatalog;

        private AgentTestRuntime(
            string rootPath,
            TestExtensionCatalog extensionCatalog,
            AgentRunCoordinator runCoordinator,
            AgentMemoryCoordinator memoryCoordinator,
            AgentSessionService sessionService,
            AgentWorkspaceService workspaceService,
            AgentPermissionService permissionService,
            AgentProfileService profileService,
            AgentAttachmentService attachmentService)
        {
            _rootPath = rootPath;
            _extensionCatalog = extensionCatalog;
            RunCoordinator = runCoordinator;
            MemoryCoordinator = memoryCoordinator;
            SessionService = sessionService;
            WorkspaceService = workspaceService;
            PermissionService = permissionService;
            ProfileService = profileService;
            AttachmentService = attachmentService;
        }

        public AgentRunCoordinator RunCoordinator { get; }

        public AgentMemoryCoordinator MemoryCoordinator { get; }

        public AgentSessionService SessionService { get; }

        public AgentWorkspaceService WorkspaceService { get; }

        public AgentPermissionService PermissionService { get; }

        public AgentProfileService ProfileService { get; }

        public AgentAttachmentService AttachmentService { get; }

        public TestExtensionCatalog ExtensionCatalog => _extensionCatalog;

        public string CurrentProfileId { get; private set; } = string.Empty;

        public string CurrentWorkspaceId { get; private set; } = string.Empty;

        public AgentProfileRecord CurrentProfile => ProfileService.GetProfile(CurrentProfileId)!;

        public static AgentTestRuntime Create(IAgentChatProvider provider, params IAgentTool[] tools)
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "sunder-agent-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);

            var extensionCatalog = new TestExtensionCatalog();
            extensionCatalog.AddExtension(PackageExtensionPoints.ChatProviders, provider);
            foreach (var tool in tools)
            {
                extensionCatalog.AddExtension(PackageExtensionPoints.Tools, tool);
            }

            var packageContext = new TestPackageContext(rootPath);
            var store = new AgentLocalStore(packageContext);
            var sessionService = new AgentSessionService(store, extensionCatalog);
            var workspaceService = new AgentWorkspaceService(store);
            var permissionService = new AgentPermissionService(store, extensionCatalog);
            var executionTargetService = new AgentExecutionTargetService(extensionCatalog);
            var installedPackageToolSource = new InstalledPackageToolSource(extensionCatalog);
            var toolService = new AgentToolService(installedPackageToolSource, sessionService, workspaceService, executionTargetService, extensionCatalog);
            var profileService = new AgentProfileService(store, toolService, extensionCatalog);
            extensionCatalog.AddExtension(PackageExtensionPoints.RuntimeCatalogs, new AgentRuntimeCatalog(sessionService, profileService, workspaceService));
            var memoryCoordinator = new AgentMemoryCoordinator(sessionService, extensionCatalog);
            var promptComposer = new AgentSystemPromptComposer(extensionCatalog);
            var attachmentService = new AgentAttachmentService(packageContext);
            extensionCatalog.AddExtension(PackageExtensionPoints.AttachmentContentStores, attachmentService);
            extensionCatalog.AddExtension(PackageExtensionPoints.SessionDataCleaners, attachmentService);
            var defaultBehaviorLoop = new DefaultAgentBehaviorLoop(promptComposer, attachmentService);
            extensionCatalog.AddExtension(PackageExtensionPoints.BehaviorLoops, defaultBehaviorLoop);
            var runAttachmentStore = new AgentRunAttachmentStore(attachmentService);
            var activeRunRegistry = new AgentActiveRunRegistry();
            var runEventLogger = new AgentRunEventLogger(packageContext);
            var providerResolver = new AgentRunProviderResolver(profileService, extensionCatalog);
            var behaviorLoopResolver = new AgentBehaviorLoopResolver(extensionCatalog, defaultBehaviorLoop);
            var stopCoordinator = new AgentRunStopCoordinator(sessionService, permissionService, memoryCoordinator, activeRunRegistry, profileService);
            var behaviorLoopHostFactory = new AgentBehaviorLoopHostFactory(sessionService, toolService, permissionService, memoryCoordinator, runEventLogger, activeRunRegistry);
            var childRunSessionService = new AgentChildRunSessionService(sessionService, profileService);
            var parentRunContinuationService = new AgentParentRunContinuationService(sessionService, profileService, workspaceService, providerResolver, activeRunRegistry, behaviorLoopHostFactory, behaviorLoopResolver, childRunSessionService);
            var permissionResumeCoordinator = new AgentPermissionResumeCoordinator(sessionService, workspaceService, profileService, permissionService, providerResolver, activeRunRegistry, runEventLogger, behaviorLoopHostFactory, behaviorLoopResolver, parentRunContinuationService);
            var userMessageRunCoordinator = new AgentUserMessageRunCoordinator(sessionService, profileService, workspaceService, memoryCoordinator, runAttachmentStore, activeRunRegistry, runEventLogger, providerResolver, behaviorLoopHostFactory, behaviorLoopResolver);
            var runCoordinator = new AgentRunCoordinator(userMessageRunCoordinator, stopCoordinator, childRunSessionService, permissionResumeCoordinator);

            return new AgentTestRuntime(rootPath, extensionCatalog, runCoordinator, memoryCoordinator, sessionService, workspaceService, permissionService, profileService, attachmentService);
        }

        public void AddMemoryFeature(CapturingMemoryFeature feature)
        {
            _extensionCatalog.AddExtension(PackageExtensionPoints.PromptContextContributors, feature);
            _extensionCatalog.AddExtension(PackageExtensionPoints.LifecycleObservers, feature);
        }

        public void AddEmbeddingProvider(IAgentEmbeddingProvider provider)
            => _extensionCatalog.AddExtension(PackageExtensionPoints.EmbeddingProviders, provider);

        public async Task<Guid> CreateSessionAsync(string toolId)
        {
            var profile = await ProfileService.CreateProfileAsync("Test Profile");
            ProfileService.SaveProfile(
                profile.ProfileId,
                profile.DisplayName,
                profile.Description,
                profile.Instructions,
                profile.ChatProviderId,
                profile.ChatModelId,
                profile.EmbeddingProviderId,
                profile.EmbeddingModelId,
                selectableCapabilityAssignments:
                [
                    new AgentProfileSelectableCapabilityAssignmentRecord(AgentProfileSelectableCapabilityKinds.Tool, toolId)
                ]);

            CurrentProfileId = profile.ProfileId;
            var workspace = WorkspaceService.CreateWorkspace("Test Workspace");
            CurrentWorkspaceId = workspace.WorkspaceId;
            return SessionService.CreateSession("Test Session").SessionId;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_rootPath))
                {
                    Directory.Delete(_rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    private sealed class ScriptedProvider : IAgentChatProvider
    {
        private readonly Func<AgentProviderRequest, int, IReadOnlyList<AgentProviderStreamEvent>> _handler;
        private readonly bool _supportsMultipleToolCalls;
        private readonly IReadOnlyList<AgentModelDescriptor> _models;

        public ScriptedProvider(
            Func<AgentProviderRequest, int, AgentProviderStreamEvent> handler,
            bool supportsMultipleToolCalls = false,
            IReadOnlyList<AgentModelDescriptor>? models = null)
            : this((request, requestIndex) => [handler(request, requestIndex)], supportsMultipleToolCalls, models)
        {
        }

        public ScriptedProvider(
            Func<AgentProviderRequest, int, IReadOnlyList<AgentProviderStreamEvent>> handler,
            bool supportsMultipleToolCalls = false,
            IReadOnlyList<AgentModelDescriptor>? models = null)
        {
            _handler = handler;
            _supportsMultipleToolCalls = supportsMultipleToolCalls;
            _models = models ?? [new AgentModelDescriptor("test-model", "Test Model", 128_000, 4_096, IsRecommended: true)];
        }

        public AgentProviderDescriptor Descriptor { get; } = new(
            "test-provider",
            "Test Provider",
            [],
            SupportsStreaming: true,
            SupportsInterruptibleRuns: true);

        public List<AgentProviderRequest> Requests { get; } = [];

        public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_models);

        public ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentProviderReadiness(Descriptor.ProviderId, AgentProviderReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentProviderRunCapabilities(
                SupportsNativeToolCalling: true,
                SupportsStreamingToolCalls: true,
                SupportsMultipleToolCalls: _supportsMultipleToolCalls,
                Summary: "Test provider supports native tool calling."));

        public ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IChatClient>(new ScriptedChatClient(context, Descriptor.DisplayName, this));
        }

        private static AgentProviderRequest CloneRequest(AgentProviderRequest request)
            => new(
                request.ProviderId,
                request.ModelId,
                request.SystemInstructions,
                request.Turns.Select(CloneTurn).ToArray(),
                request.AvailableTools?.ToArray(),
                request.ReasoningEffort);

        private static AgentTurnRecord CloneTurn(AgentTurnRecord turn)
            => new(
                turn.TurnId,
                turn.SessionId,
                turn.Role,
                turn.Kind,
                turn.Items.Select(item => item with { }).ToArray(),
                turn.CreatedAtUtc,
                turn.UpdatedAtUtc);

        private sealed class ScriptedChatClient(
            AgentChatClientContext context,
            string providerDisplayName,
            ScriptedProvider provider) : IChatClient
        {
            private readonly AgentChatClientContext _context = context;
            private readonly ScriptedProvider _provider = provider;

            public ChatClientMetadata Metadata { get; } = new(providerDisplayName);

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                var responseMessage = new ChatMessage(ChatRole.Assistant, []);
                await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
                {
                    foreach (var content in update.Contents)
                    {
                        responseMessage.Contents.Add(content);
                    }
                }

                return new ChatResponse(responseMessage)
                {
                    ModelId = options?.ModelId ?? _context.ModelId,
                };
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = BuildProviderRequest(_context, messages, options);
                var capturedRequest = CloneRequest(request);
                _provider.Requests.Add(capturedRequest);
                var requestIndex = _provider.Requests.Count;
                var responseId = Guid.NewGuid().ToString("N");
                var messageId = responseId;
                var modelId = options?.ModelId ?? _context.ModelId;
                foreach (var streamEvent in _provider._handler(capturedRequest, requestIndex))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (streamEvent.Type)
                    {
                        case AgentProviderStreamEventType.TextDelta when streamEvent.Delta is not null:
                            yield return new ChatResponseUpdate(ChatRole.Assistant, streamEvent.Delta)
                            {
                                ResponseId = responseId,
                                MessageId = messageId,
                                ModelId = modelId,
                            };
                            break;

                        case AgentProviderStreamEventType.ToolCallRequested when streamEvent.ToolCalls is { Count: > 0 }:
                            yield return CreateToolCallUpdate(streamEvent.ToolCalls, responseId, messageId, modelId);
                            yield break;

                        case AgentProviderStreamEventType.ToolCallRequested when streamEvent.ToolCall is not null:
                            yield return CreateToolCallUpdate(streamEvent.ToolCall, responseId, messageId, modelId);
                            yield break;

                        case AgentProviderStreamEventType.Completed when streamEvent.Response is not null:
                            if (!string.IsNullOrWhiteSpace(streamEvent.Response.Content))
                            {
                                yield return new ChatResponseUpdate(ChatRole.Assistant, streamEvent.Response.Content)
                                {
                                    ResponseId = responseId,
                                    MessageId = messageId,
                                    ModelId = modelId,
                                };
                            }
                            break;

                        case AgentProviderStreamEventType.Error when streamEvent.Response is not null:
                            throw new AgentChatProviderException(
                                streamEvent.Response.ErrorCode ?? "Provider response failed.",
                                streamEvent.Response.Content,
                                streamEvent.Response.ErrorCode);
                    }
                }

                await Task.CompletedTask;
            }

            public object? GetService(Type serviceType, object? serviceKey = null)
                => serviceKey is null && serviceType.IsInstanceOfType(this)
                    ? this
                    : serviceKey is null && serviceType == typeof(ChatClientMetadata)
                        ? Metadata
                        : null;

            public void Dispose()
            {
            }

            private static AgentProviderRequest BuildProviderRequest(
                AgentChatClientContext context,
                IEnumerable<ChatMessage> messages,
                ChatOptions? options)
                => new(
                    context.ProviderId,
                    options?.ModelId ?? context.ModelId,
                    options?.Instructions,
                    BuildProviderTurns(messages, options?.ConversationId),
                    options?.ToolMode == ChatToolMode.None ? null : BuildProviderToolDescriptors(options?.Tools),
                    options?.Reasoning?.Effort);

            private static IReadOnlyList<AgentTurnRecord> BuildProviderTurns(IEnumerable<ChatMessage> messages, string? conversationId)
            {
                var sessionId = Guid.TryParse(conversationId, out var parsedSessionId)
                    ? parsedSessionId
                    : Guid.Empty;
                var toolIdsByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
                var turns = new List<AgentTurnRecord>();
                foreach (var message in messages)
                {
                    turns.AddRange(BuildProviderTurns(message, sessionId, toolIdsByCallId));
                }

                return turns;
            }

            private static IReadOnlyList<AgentTurnRecord> BuildProviderTurns(
                ChatMessage message,
                Guid sessionId,
                IDictionary<string, string> toolIdsByCallId)
            {
                var turns = new List<AgentTurnRecord>();
                var textBuilder = new StringBuilder();
                foreach (var content in message.Contents)
                {
                    switch (content)
                    {
                        case TextContent textContent when !string.IsNullOrWhiteSpace(textContent.Text):
                            AppendText(textBuilder, textContent.Text);
                            break;

                        case FunctionCallContent functionCall:
                            FlushProviderTextTurn(message, sessionId, turns, textBuilder);
                            if (!string.IsNullOrWhiteSpace(functionCall.CallId) && !string.IsNullOrWhiteSpace(functionCall.Name))
                            {
                                toolIdsByCallId[functionCall.CallId] = functionCall.Name;
                            }

                            turns.Add(CreateProviderTurn(
                                message,
                                sessionId,
                                ToAgentRole(message.Role),
                                AgentTurnKind.ToolCall,
                                new AgentTurnItemRecord(
                                    Guid.NewGuid(),
                                    Guid.Empty,
                                    0,
                                    AgentTurnItemKind.ToolCall,
                                    null,
                                    functionCall.CallId,
                                    functionCall.Name,
                                    SerializeArguments(functionCall.Arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal)),
                                    null,
                                    null,
                                    null,
                                    false,
                                    false,
                                    null,
                                    null)));
                            break;

                        case FunctionResultContent functionResult:
                            FlushProviderTextTurn(message, sessionId, turns, textBuilder);
                            var resultText = RenderFunctionResult(functionResult.Result);
                            toolIdsByCallId.TryGetValue(functionResult.CallId, out var toolId);
                            turns.Add(CreateProviderTurn(
                                message,
                                sessionId,
                                AgentMessageRole.Tool,
                                AgentTurnKind.ToolResult,
                                new AgentTurnItemRecord(
                                    Guid.NewGuid(),
                                    Guid.Empty,
                                    0,
                                    AgentTurnItemKind.ToolResult,
                                    resultText,
                                    functionResult.CallId,
                                    toolId,
                                    null,
                                    resultText,
                                    null,
                                    null,
                                    false,
                                    functionResult.Exception is not null,
                                    functionResult.Exception?.GetType().Name,
                                    null)));
                            break;
                    }
                }

                if (textBuilder.Length == 0 && !string.IsNullOrWhiteSpace(message.Text))
                {
                    textBuilder.Append(message.Text);
                }

                FlushProviderTextTurn(message, sessionId, turns, textBuilder);
                return turns;
            }

            private static void FlushProviderTextTurn(
                ChatMessage message,
                Guid sessionId,
                ICollection<AgentTurnRecord> turns,
                StringBuilder textBuilder)
            {
                if (textBuilder.Length == 0)
                {
                    return;
                }

                turns.Add(CreateProviderTurn(
                    message,
                    sessionId,
                    ToAgentRole(message.Role),
                    AgentTurnKind.Message,
                    new AgentTurnItemRecord(
                        Guid.NewGuid(),
                        Guid.Empty,
                        0,
                        AgentTurnItemKind.Text,
                        textBuilder.ToString(),
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        false,
                        false,
                        null,
                        null)));
                textBuilder.Clear();
            }

            private static AgentTurnRecord CreateProviderTurn(
                ChatMessage message,
                Guid sessionId,
                AgentMessageRole role,
                AgentTurnKind kind,
                AgentTurnItemRecord item)
            {
                var turnId = Guid.TryParse(message.MessageId, out var parsedTurnId)
                    ? parsedTurnId
                    : Guid.NewGuid();
                var createdAt = message.CreatedAt ?? DateTimeOffset.UtcNow;
                return new AgentTurnRecord(
                    turnId,
                    sessionId,
                    role,
                    kind,
                    [item with { TurnId = turnId }],
                    createdAt,
                    createdAt);
            }

            private static IReadOnlyList<AgentToolDescriptor>? BuildProviderToolDescriptors(IList<AITool>? tools)
                => tools is { Count: > 0 }
                    ? tools.Select(tool => new AgentToolDescriptor(
                            tool.Name,
                            tool.Name,
                            tool.Description ?? string.Empty,
                            ArgumentsJsonSchema: tool is AIFunctionDeclaration functionDeclaration
                                && functionDeclaration.JsonSchema.ValueKind != JsonValueKind.Undefined
                                ? functionDeclaration.JsonSchema.GetRawText()
                                : null))
                        .ToArray()
                    : null;

            private static AgentMessageRole ToAgentRole(ChatRole role)
                => role == ChatRole.System
                    ? AgentMessageRole.System
                    : role == ChatRole.Assistant
                        ? AgentMessageRole.Assistant
                        : role == ChatRole.Tool
                            ? AgentMessageRole.Tool
                            : AgentMessageRole.User;

            private static ChatResponseUpdate CreateToolCallUpdate(
                AgentToolCallRequest toolCall,
                string responseId,
                string messageId,
                string modelId)
                => CreateToolCallUpdate([toolCall], responseId, messageId, modelId);

            private static ChatResponseUpdate CreateToolCallUpdate(
                IReadOnlyList<AgentToolCallRequest> toolCalls,
                string responseId,
                string messageId,
                string modelId)
                => new(ChatRole.Assistant, toolCalls
                    .Select(toolCall => new FunctionCallContent(toolCall.CallId, toolCall.ToolId, ParseArguments(toolCall.ArgumentsJson)))
                    .ToArray())
                {
                    ResponseId = responseId,
                    MessageId = messageId,
                    ModelId = modelId,
                };

            private static void AppendText(StringBuilder builder, string text)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine();
                }

                builder.Append(text);
            }

            private static IDictionary<string, object?> ParseArguments(string argumentsJson)
            {
                if (string.IsNullOrWhiteSpace(argumentsJson))
                {
                    return new Dictionary<string, object?>(StringComparer.Ordinal);
                }

                try
                {
                    using var document = JsonDocument.Parse(argumentsJson);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        return new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["value"] = document.RootElement.Clone(),
                        };
                    }

                    return document.RootElement.EnumerateObject()
                        .ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
                }
                catch (JsonException)
                {
                    return new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["value"] = argumentsJson,
                    };
                }
            }

            private static string SerializeArguments(IDictionary<string, object?> arguments)
                => arguments.Count == 0
                    ? "{}"
                    : JsonSerializer.Serialize(arguments);

            private static string RenderFunctionResult(object? result)
                => result switch
                {
                    null => string.Empty,
                    string text => text,
                    JsonElement jsonElement => jsonElement.GetRawText(),
                    _ => JsonSerializer.Serialize(result),
                };
        }
    }

    private sealed class TestTool(string toolId, IReadOnlyList<string>? aliases = null) : IAgentTool
    {
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public string? LastWorkspaceId { get; private set; }

        public AgentToolDescriptor Descriptor { get; } = new(
            toolId,
            "Test Tool",
            "Returns deterministic tool output.",
            IsReadOnly: true,
            RequiresNetwork: false,
            ArgumentsJsonSchema: "{\"type\":\"object\"}",
            Aliases: aliases);

        public ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolReadiness(Descriptor.ToolId, AgentToolReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executionCount);
            LastWorkspaceId = context.Workspace?.WorkspaceId;
            return ValueTask.FromResult(new AgentToolResult(
                request.ToolId,
                $"Executed {request.ToolId}.",
                Content: $"Tool output for {request.ArgumentsJson}"));
        }
    }

    private sealed class MetadataTool(string toolId, string sourceDisplayName) : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; } = new(
            toolId,
            "Metadata Tool",
            "Returns deterministic tool output.",
            IsReadOnly: true,
            RequiresNetwork: false,
            ArgumentsJsonSchema: "{\"type\":\"object\"}",
            SourceKind: "metadata",
            SourceId: "metadata-tools",
            SourceDisplayName: sourceDisplayName);

        public ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolReadiness(Descriptor.ToolId, AgentToolReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolResult(
                request.ToolId,
                $"Executed {request.ToolId}.",
                Content: $"Tool output for {request.ArgumentsJson}"));
    }

    private sealed class TestMutableTool(string toolId) : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; } = new(
            toolId,
            "Mutable Test Tool",
            "Returns deterministic mutating tool output.",
            IsReadOnly: false,
            RequiresNetwork: false,
            ArgumentsJsonSchema: "{\"type\":\"object\"}");

        public ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolReadiness(Descriptor.ToolId, AgentToolReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolResult(
                request.ToolId,
                $"Executed {request.ToolId}.",
                Content: $"Tool output for {request.ArgumentsJson}"));
    }

    private sealed class ErrorResultTool(string toolId, string errorCode) : IAgentTool
    {
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public AgentToolDescriptor Descriptor { get; } = new(
            toolId,
            "Error Result Tool",
            "Returns deterministic errored tool output.",
            IsReadOnly: true,
            RequiresNetwork: false,
            ArgumentsJsonSchema: "{\"type\":\"object\"}");

        public ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolReadiness(Descriptor.ToolId, AgentToolReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executionCount);
            return ValueTask.FromResult(new AgentToolResult(
                request.ToolId,
                "Shell command exited with code 128",
                Content: "fatal: not a git repository (or any parent up to mount point /)\nStopping at filesystem boundary (GIT_DISCOVERY_ACROSS_FILESYSTEM not set).",
                IsError: true,
                ErrorCode: errorCode));
        }
    }

    private sealed class GenericErrorResultTool(string toolId, string errorCode, string content) : IAgentTool
    {
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public AgentToolDescriptor Descriptor { get; } = new(
            toolId,
            "Generic Error Result Tool",
            "Returns deterministic errored tool output.",
            IsReadOnly: true,
            RequiresNetwork: false,
            ArgumentsJsonSchema: "{\"type\":\"object\"}");

        public ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolReadiness(Descriptor.ToolId, AgentToolReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executionCount);
            return ValueTask.FromResult(new AgentToolResult(
                request.ToolId,
                content,
                Content: content,
                IsError: true,
                ErrorCode: errorCode));
        }
    }

    private sealed class ThrowingTool(string toolId, string message) : IAgentTool
    {
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public AgentToolDescriptor Descriptor { get; } = new(
            toolId,
            "Throwing Tool",
            "Throws deterministic tool exceptions.",
            IsReadOnly: true,
            RequiresNetwork: false,
            ArgumentsJsonSchema: "{\"type\":\"object\"}");

        public ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolReadiness(Descriptor.ToolId, AgentToolReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executionCount);
            throw new InvalidOperationException(message);
        }
    }

    private sealed class PermissionedToolSource(string toolId) : IAgentToolSource, IAgentPermissionAwareToolSource, IAgentPermissionSurface
    {
        private const string ActionId = "test.permissioned.execute";
        private const string BoundaryId = "test.permissioned.boundary";

        public const string ActionIdForTests = ActionId;

        public const string BoundaryIdForTests = BoundaryId;

        public int ExecutionCount { get; private set; }

        public string SourceId => "test-permissioned-source";

        public string DisplayName => "Test Permissioned Source";

        public string SourceKind => "test";

        public string SurfaceId => "test-permissioned";

        private AgentToolDescriptor Descriptor { get; } = new(
            toolId,
            "Approval Tool",
            "Requires approval before execution.",
            IsReadOnly: false,
            ArgumentsJsonSchema: "{\"type\":\"object\"}",
            SourceKind: "test",
            SourceId: "test-permissioned-source",
            SourceDisplayName: "Test Permissioned Source");

        public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
            AgentToolSourceContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>([Descriptor]);

        public ValueTask<AgentToolReadiness?> GetReadinessAsync(
            string requestedToolId,
            AgentToolSourceContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentToolReadiness?>(
                string.Equals(requestedToolId, toolId, StringComparison.OrdinalIgnoreCase)
                    ? new AgentToolReadiness(toolId, AgentToolReadinessStatus.Ready, "Ready.")
                    : null);

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return ValueTask.FromResult(new AgentToolResult(
                request.ToolId,
                "Executed approval tool.",
                Content: $"Approved output for {request.ArgumentsJson}"));
        }

        public ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentPermissionRequest?>(new AgentPermissionRequest(
                ActionId,
                BoundaryId,
                "Execute approval tool",
                ToolId: request.ToolId,
                WorkspaceId: context.Workspace?.WorkspaceId,
                BindingId: context.ExecutionBinding?.BindingId,
                IsMutation: true));

        public IReadOnlyList<AgentPermissionActionDescriptor> ListActions()
            =>
            [
                new(ActionId, "Execute approval tool", "Execute a test tool that requires approval.",
                [
                    new(BoundaryId, "Approval boundary", "Requires explicit approval.", AgentPermissionDecision.Ask),
                ]),
            ];
    }

    private sealed class TestSystemPromptContributor : IAgentSystemPromptContributor
    {
        public string ContributorId => "test-contributor";

        public string DisplayName => "Test Contributor";

        public ValueTask<IReadOnlyList<AgentSystemPromptBlock>> ContributeAsync(
            AgentSystemPromptRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentSystemPromptBlock>>(
            [
                new(
                    "test-block",
                    "Test Contributor Block",
                    "Contributor-provided runtime guidance.",
                    Priority: 50,
                    SourceId: ContributorId),
            ]);
    }

    private sealed class TestScopedExecutionTarget : IAgentExecutionTarget, IAgentExecutionScopeProvider
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            "test",
            "test-target",
            "Test Target",
            "Test execution target.",
            SupportsShell: true,
            SupportsFiles: true,
            SupportsSearch: true);

        public ValueTask<AgentExecutionScopeDescriptor> GetExecutionScopeAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionScopeDescriptor(
                "Test Target",
                ["C:\\Users\\micha\\Downloads\\ROZANA\\ROZANA"],
                "C:\\Users\\micha\\Downloads\\ROZANA\\ROZANA",
                "Windows local filesystem paths."));

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class TestExtensionCatalog : IPackageExtensionCatalog, IPackageExtensionCatalogChangeNotifier, IPackageExtensionCatalogMonitor
    {
        private readonly Dictionary<string, List<object>> _extensions = new(StringComparer.OrdinalIgnoreCase);

        private long _revision;

        public event EventHandler? ExtensionsChanged;

        public event EventHandler<PackageExtensionCatalogChangedEventArgs>? Changed;

        public void AddExtension<TContract>(PackageExtensionPoint<TContract> extensionPoint, TContract extension)
        {
            if (!_extensions.TryGetValue(extensionPoint.Id, out var entries))
            {
                entries = [];
                _extensions[extensionPoint.Id] = entries;
            }

            entries.Add(extension!);
            var args = new PackageExtensionCatalogChangedEventArgs(
                Interlocked.Increment(ref _revision),
                PackageExtensionCatalogChangeReason.PackageActivated,
                [new PackageExtensionChange("test.package", extensionPoint.Id, PackageExtensionChangeKind.Added, extension?.GetType())]);
            ExtensionsChanged?.Invoke(this, EventArgs.Empty);
            Changed?.Invoke(this, args);
        }

        public IReadOnlyList<TContract> GetExtensions<TContract>(PackageExtensionPoint<TContract> extensionPoint)
            => !_extensions.TryGetValue(extensionPoint.Id, out var entries)
                ? []
                : entries.Cast<TContract>().ToArray();
    }

    private sealed class MutableSelectableCapabilityProvider : IAgentProfileSelectableCapabilityProvider, IAgentProfileSelectableCapabilityChangeNotifier
    {
        public string ProviderId => "mutable-capabilities";

        public string SourceId => "mutable-capabilities";

        public string SourceKind => "test";

        public string DisplayName => "Mutable Capabilities";

        public event Action? SelectableCapabilitiesChanged;

        public ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(
            AgentProfileSelectableCapabilityRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([]);

        public void RaiseChanged()
            => SelectableCapabilitiesChanged?.Invoke();
    }

    private sealed class TestRuntimeCatalog(
        IReadOnlyList<AgentProfileRecord> profiles,
        IReadOnlyList<AgentSessionRecord> sessions,
        IReadOnlyList<AgentWorkspaceRecord>? workspaces = null) : IAgentRuntimeCatalog
    {
        private readonly IReadOnlyList<AgentProfileRecord> _profiles = profiles;
        private readonly IReadOnlyList<AgentSessionRecord> _sessions = sessions;
        private readonly IReadOnlyList<AgentWorkspaceRecord> _workspaces = workspaces ?? [];

        public event Action<string>? ProfileChanged
        {
            add { }
            remove { }
        }

        public event Action<Guid>? SessionChanged
        {
            add { }
            remove { }
        }

        public event Action<Guid, AgentTurnRecord>? TurnChanged
        {
            add { }
            remove { }
        }

        public AgentSessionRecord Session => _sessions[0];

        public IReadOnlyList<AgentSessionRecord> ListSessions() => _sessions;

        public IReadOnlyList<AgentSessionRecord> ListSessionsForProfile(string profileId)
            => [];

        public AgentSessionRecord? GetSession(Guid sessionId)
            => _sessions.FirstOrDefault(session => session.SessionId == sessionId);

        public IReadOnlyList<AgentWorkspaceRecord> ListWorkspaces() => _workspaces;

        public AgentWorkspaceRecord? GetWorkspace(string workspaceId)
            => _workspaces.FirstOrDefault(workspace => string.Equals(workspace.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase));

        public AgentProfileRecord? GetSessionProfile(Guid sessionId) => null;

        public AgentWorkingSummaryRecord? GetWorkingSummary(Guid sessionId) => null;

        public AgentRunCheckpointRecord? GetLatestCheckpoint(Guid sessionId) => null;

        public IReadOnlyList<AgentTurnRecord> ListRecentTurns(Guid sessionId, int limit) => [];

        public IReadOnlyList<AgentTurnRecord> ListTurnsBefore(Guid sessionId, DateTimeOffset beforeCreatedAtUtc, Guid beforeTurnId, int limit) => [];

        public IReadOnlyList<AgentProfileRecord> ListProfiles() => _profiles;

        public AgentProfileRecord? GetProfile(string profileId)
            => _profiles.FirstOrDefault(profile => string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));

        public AgentProfileModelBindingRecord? GetSessionModelBinding(Guid sessionId, string capabilityKind) => null;

        public AgentProfileModelBindingRecord? GetModelBinding(string profileId, string capabilityKind) => null;
    }

    private sealed class CapturingChildRunExecutor : IAgentChildRunExecutor
    {
        public AgentChildRunRequest? Request { get; private set; }

        public List<AgentChildRunRequest> Requests { get; } = [];

        public ValueTask<AgentChildRunResult> RunChildAsync(
            AgentChildRunRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            Requests.Add(request);
            return ValueTask.FromResult(new AgentChildRunResult(Guid.NewGuid(), AgentRunStatus.Completed, "Child completed.", "Child result."));
        }
    }

    private sealed class CapturingShellViewService : IPackageShellViewService
    {
        public string? OpenedViewId { get; private set; }

        public IReadOnlyDictionary<string, string?>? Parameters { get; private set; }

        public IReadOnlyList<PackageHotbarView> ListHotbarViews() => [];

        public bool IsViewInHotbar(string viewId) => false;

        public ValueTask<bool> AddViewToDefaultHotbarAsync(
            string viewId,
            bool openPanel = false,
            IReadOnlyDictionary<string, string?>? parameters = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<bool> AddViewToHotbarAsync(
            string viewId,
            PackageHotbarPlacement placement,
            int? index = null,
            bool openPanel = false,
            IReadOnlyDictionary<string, string?>? parameters = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<bool> RemoveViewFromHotbarAsync(string viewId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<bool> OpenViewPanelAsync(
            string viewId,
            IReadOnlyDictionary<string, string?>? parameters = null,
            CancellationToken cancellationToken = default)
        {
            OpenedViewId = viewId;
            Parameters = parameters;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> CloseViewPanelAsync(string viewId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }

    private sealed class TestEmbeddingProvider(string providerId) : IAgentEmbeddingProvider
    {
        public AgentEmbeddingProviderDescriptor Descriptor { get; } = new(providerId, "Test Embeddings", []);

        public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([new AgentEmbeddingModelDescriptor("semantic-v1", "Semantic V1", Dimensions: 2, IsRecommended: true)]);

        public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentEmbeddingProviderReadiness(Descriptor.ProviderId, AgentProviderReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(string modelId, string text, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentEmbeddingGenerationResult?>(new AgentEmbeddingGenerationResult(modelId, CreateVector(text)));

        public ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(string modelId, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingGenerationResult?>>(texts.Select(text => (AgentEmbeddingGenerationResult?)new AgentEmbeddingGenerationResult(modelId, CreateVector(text))).ToArray());

        private static IReadOnlyList<float> CreateVector(string text)
        {
            var normalized = text.Trim().ToLowerInvariant();
            if (normalized.Contains("concise", StringComparison.Ordinal) || normalized.Contains("brief", StringComparison.Ordinal))
            {
                return [1f, 0f];
            }

            if (normalized.Contains("verbose", StringComparison.Ordinal) || normalized.Contains("detailed", StringComparison.Ordinal))
            {
                return [0f, 1f];
            }

            return [0.1f, 0.1f];
        }
    }

    private sealed class ThrowingEmbeddingProvider(string providerId) : IAgentEmbeddingProvider
    {
        public AgentEmbeddingProviderDescriptor Descriptor { get; } = new(providerId, "Throwing Embeddings", []);

        public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([new AgentEmbeddingModelDescriptor("semantic-v1", "Semantic V1")]);

        public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentEmbeddingProviderReadiness(Descriptor.ProviderId, AgentProviderReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(string modelId, string text, CancellationToken cancellationToken = default)
            => ValueTask.FromException<AgentEmbeddingGenerationResult?>(new InvalidOperationException("Embedding generation failed."));

        public ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(string modelId, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
            => ValueTask.FromException<IReadOnlyList<AgentEmbeddingGenerationResult?>>(new InvalidOperationException("Embedding generation failed."));
    }

    private sealed class TestPackageContext(
        string rootPath,
        IReadOnlyDictionary<string, string>? configurationValues = null,
        IReadOnlyDictionary<string, string>? secretValues = null) : IPackageContext
    {
        private readonly TestPackageStorageContext _storage = new(rootPath);
        private readonly InMemoryPackageConfiguration _configuration = new(configurationValues);
        private readonly InMemoryPackageSecrets _secrets = new(secretValues);

        public string PackageId => "test.package.agent";

        public Version Version => new(1, 0, 0);

        public string InstallPath => rootPath;

        public IPackageStorageContext Storage => _storage;

        public IPackageConfiguration Configuration => _configuration;

        public IPackageSecrets Secrets => _secrets;

        public Microsoft.Extensions.Logging.ILoggerFactory LoggerFactory => Logging.LoggerFactory;

        public Sunder.Sdk.Logging.IPackageLogging Logging { get; } = Sunder.Sdk.Logging.NullPackageLogging.Instance;
    }

    private sealed class TestPackageStorageContext : IPackageStorageContext
    {
        public TestPackageStorageContext(string rootPath)
        {
            DataRootPath = Path.Combine(rootPath, "data");
            CacheRootPath = Path.Combine(rootPath, "cache");
            LogsRootPath = Path.Combine(rootPath, "logs");
            Directory.CreateDirectory(DataRootPath);
            Directory.CreateDirectory(CacheRootPath);
            Directory.CreateDirectory(LogsRootPath);
            Files = new NullPackageFileStore(DataRootPath);
            State = new NullPackageKeyValueStore();
        }

        public string DataRootPath { get; }

        public string CacheRootPath { get; }

        public string LogsRootPath { get; }

        public IPackageFileStore Files { get; }

        public IPackageKeyValueStore State { get; }
    }

    private sealed class NullPackageFileStore(string rootPath) : IPackageFileStore
    {
        public string RootPath { get; } = rootPath;

        public string GetPath(string relativePath) => Path.Combine(RootPath, relativePath);
    }

    private sealed class NullPackageKeyValueStore : IPackageKeyValueStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? GetValue(string key) => _values.GetValueOrDefault(key);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(GetValue(key));

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.ContainsKey(key));

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_values.Keys.Where(key => prefix is null || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    private sealed class InMemoryPackageConfiguration(IReadOnlyDictionary<string, string>? values) : IPackageConfiguration
    {
        private readonly IReadOnlyDictionary<string, string> _values = values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string? GetValue(string key)
            => _values.TryGetValue(key, out var value) ? value : null;
    }

    private sealed class InMemoryPackageSecrets(IReadOnlyDictionary<string, string>? values) : IPackageSecrets
    {
        private readonly Dictionary<string, string> _values = new(values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        public string? GetSecret(string key)
            => _values.TryGetValue(key, out var value) ? value : null;

        public void SetSecret(string key, string value)
        {
            _values[key] = value;
        }

        public void DeleteSecret(string key)
        {
            _values.Remove(key);
        }
    }

    private sealed class CapturingMemoryFeature : IAgentPromptContextContributor, IAgentLifecycleObserver
    {
        public string FeatureId => "test.memory";

        public string DisplayName => "Test Memory";

        public string ContributorId => FeatureId;

        public string ObserverId => FeatureId;

        public AgentMemoryRecallRequest? LastRecallRequest { get; private set; }

        public AgentLifecycleEvent? LastLifecycleEvent { get; private set; }

        public AgentMemoryRecallResult? RecallResult { get; init; }

        public ValueTask<AgentPromptContextContribution?> ContributeContextAsync(AgentPromptContextRequest request, CancellationToken cancellationToken = default)
        {
            if (!request.ContextPlan.ShouldContribute)
            {
                return ValueTask.FromResult<AgentPromptContextContribution?>(null);
            }

            LastRecallRequest = new AgentMemoryRecallRequest(
                request.Session,
                request.Run,
                request.Turn,
                request.Turns,
                request.RecentLiveBufferTurns,
                ToMemoryRecallPlan(request.ContextPlan));

            return RecallResult is null || RecallResult.Entries.Count == 0
                ? ValueTask.FromResult<AgentPromptContextContribution?>(null)
                : ValueTask.FromResult<AgentPromptContextContribution?>(new AgentPromptContextContribution(
                [
                    new AgentPromptContextBlock(
                        "Recalled Session Context",
                        BuildRecallContextBlock(RecallResult),
                        Priority: 100,
                        SourceId: FeatureId),
                ]));
        }

        public ValueTask<AgentLifecycleObserverResult?> HandleLifecycleEventAsync(AgentLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default)
        {
            LastLifecycleEvent = lifecycleEvent;
            return ValueTask.FromResult<AgentLifecycleObserverResult?>(null);
        }

        private static AgentMemoryRecallPlan ToMemoryRecallPlan(AgentPromptContextPlan plan)
            => Enum.TryParse<AgentMemoryRecallIntent>(plan.Intent, ignoreCase: true, out var intent)
                ? new AgentMemoryRecallPlan(intent, plan.QueryText, plan.Reason, plan.PreferredCategories, plan.MaxEntryCount, plan.MaxChars)
                : AgentMemoryRecallPlan.None(plan.Reason);

        private static string BuildRecallContextBlock(AgentMemoryRecallResult recallResult)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Use this context when it is relevant. Prefer direct current-turn user instructions if there is a conflict.");
            foreach (var entry in recallResult.Entries)
            {
                builder.Append("- [").Append(entry.Category).Append(" | ").Append(entry.TrustState).Append("] ").AppendLine(entry.Content.Trim());
                if (!string.IsNullOrWhiteSpace(entry.EvidenceText))
                {
                    builder.Append("  Evidence: ").AppendLine(entry.EvidenceText.Trim());
                }

                if (entry.SourceTurnId is Guid sourceTurnId)
                {
                    builder.Append("  Source turn: `").Append(sourceTurnId).AppendLine("`");
                }

                if (entry.MatchReasons is { Count: > 0 })
                {
                    builder.Append("  Why recalled: ")
                        .AppendLine(string.Join("; ", entry.MatchReasons.Select(reason => reason.Description.Trim())));
                }
            }

            return builder.ToString().Trim();
        }
    }
}
