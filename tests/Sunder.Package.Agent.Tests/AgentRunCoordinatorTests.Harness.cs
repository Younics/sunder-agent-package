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
extern alias McpApp;

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
using McpAppManagementService = McpApp::Sunder.Generated.Packages.Sunder_Package_Agent_Mcp_Services.IManagementService;
using McpAppRuntimeGateway = McpApp::Sunder.Package.Agent.Mcp.Runtime.McpAppRuntimeGateway;

namespace Sunder.Package.Agent.Tests;

using AgentMcpSettingsViewModel = McpApp::Sunder.Package.Agent.Mcp.AgentMcpSettingsViewModel;
using McpStatusKind = McpApp::Sunder.Package.Agent.Mcp.McpStatusKind;

public sealed partial class AgentRunCoordinatorTests
{

    private sealed record AgentProviderRequest(
        string ProviderId,
        string ModelId,
        string? SystemInstructions,
        IReadOnlyList<AgentTurnRecord> Turns,
        IReadOnlyList<AgentToolDescriptor>? AvailableTools = null,
        ReasoningEffort? ReasoningEffort = null,
        ReasoningOutput? ReasoningOutput = null
    );

    private sealed record AgentProviderStreamEvent(
        AgentProviderStreamEventType Type,
        string? Delta = null,
        AgentProviderResponse? Response = null,
        AgentToolCallRequest? ToolCall = null,
        IReadOnlyList<AgentToolCallRequest>? ToolCalls = null
    );

    private enum AgentProviderStreamEventType
    {
        TextDelta = 0,
        Completed = 1,
        Error = 2,
        ToolCallRequested = 3,
        ReasoningDelta = 4,
    }

    private sealed record AgentProviderResponse(
        string Content,
        bool IsError = false,
        string? ErrorCode = null,
        AgentChatProviderFailureKind FailureKind = AgentChatProviderFailureKind.Unknown
    );

    private static AgentProviderStreamEvent AssertAndComplete(
        AgentProviderRequest request,
        string toolId,
        string callId
    )
    {
        Assert.Contains(
            request.Turns,
            turn =>
                turn.Kind == AgentTurnKind.ToolResult
                && turn.Items.Any(item =>
                    item.Kind == AgentTurnItemKind.ToolResult
                    && item.ToolId == toolId
                    && item.CallId == callId
                )
        );
        AssertNoOrphanToolResults(request);

        return Complete("Used the tool result without refetching.");
    }

    private static AgentProviderStreamEvent AssertToolResultContentAndComplete(
        AgentProviderRequest request,
        string toolId,
        string callId,
        string expectedContent,
        string unexpectedContent
    )
    {
        var toolResult = request.Turns
            .Where(turn => turn.Kind == AgentTurnKind.ToolResult)
            .SelectMany(turn => turn.Items)
            .Single(item => item.Kind == AgentTurnItemKind.ToolResult
                            && string.Equals(item.ToolId, toolId, StringComparison.Ordinal)
                            && string.Equals(item.CallId, callId, StringComparison.Ordinal));
        Assert.NotNull(toolResult.TextContent);
        Assert.Contains(expectedContent, toolResult.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(unexpectedContent, toolResult.TextContent, StringComparison.Ordinal);
        AssertNoOrphanToolResults(request);

        return Complete("Used the visible tool result content.");
    }

    private static AgentProviderStreamEvent AssertAmbiguousToolResultAndComplete(
        AgentProviderRequest request,
        string toolId,
        string callId)
    {
        var result = request.Turns
            .Where(turn => turn.Kind == AgentTurnKind.ToolResult)
            .SelectMany(turn => turn.Items)
            .Single(item => item.Kind == AgentTurnItemKind.ToolResult
                            && item.ToolId == toolId
                            && item.CallId == callId);
        Assert.Contains("[Sunder execution status: AMBIGUOUS]", result.TextContent, StringComparison.Ordinal);
        Assert.Contains(AgentToolResultErrorCodes.ToolOutcomeAmbiguous, result.TextContent, StringComparison.Ordinal);
        Assert.Contains("Effects may have occurred", result.TextContent, StringComparison.Ordinal);
        Assert.Contains("--- Untrusted tool output ---", result.TextContent, StringComparison.Ordinal);
        return Complete("The mutation outcome was uncertain and requires verification.");
    }

    private static AgentProviderStreamEvent AssertUntrustedFailureMetadataAndComplete(
        AgentProviderRequest request,
        string toolId,
        string callId,
        string maliciousSummary,
        string maliciousErrorCode)
    {
        const string boundary = "--- Untrusted tool output ---";
        var result = request.Turns
            .Where(turn => turn.Kind == AgentTurnKind.ToolResult)
            .SelectMany(turn => turn.Items)
            .Single(item => item.Kind == AgentTurnItemKind.ToolResult
                            && item.ToolId == toolId
                            && item.CallId == callId);
        var content = Assert.IsType<string>(result.TextContent);
        var boundaryIndex = content.IndexOf(boundary, StringComparison.Ordinal);
        Assert.True(boundaryIndex >= 0, content);
        var trusted = content[..boundaryIndex];
        var untrusted = content[(boundaryIndex + boundary.Length)..];
        Assert.Contains("[Sunder execution status: FAILED]", trusted, StringComparison.Ordinal);
        Assert.DoesNotContain(maliciousSummary, trusted, StringComparison.Ordinal);
        Assert.DoesNotContain(maliciousErrorCode, trusted, StringComparison.Ordinal);
        Assert.Contains(maliciousSummary, untrusted, StringComparison.Ordinal);
        Assert.Contains(maliciousErrorCode, untrusted, StringComparison.Ordinal);
        return Complete("Treated the tool metadata as untrusted.");
    }

    private static AgentProviderStreamEvent AssertUntrustedSuccessfulOutputAndComplete(
        AgentProviderRequest request,
        string toolId,
        string callId,
        string maliciousSummary,
        string maliciousErrorCode,
        string maliciousContent)
    {
        const string boundary = "--- Untrusted tool output ---";
        var result = request.Turns
            .Where(turn => turn.Kind == AgentTurnKind.ToolResult)
            .SelectMany(turn => turn.Items)
            .Single(item => item.Kind == AgentTurnItemKind.ToolResult
                            && item.ToolId == toolId
                            && item.CallId == callId);
        var content = Assert.IsType<string>(result.TextContent);
        var boundaryIndex = content.IndexOf(boundary, StringComparison.Ordinal);
        Assert.True(boundaryIndex >= 0, content);
        var trusted = content[..boundaryIndex];
        var untrusted = content[(boundaryIndex + boundary.Length)..];
        Assert.Contains("[Sunder execution status: COMPLETED]", trusted, StringComparison.Ordinal);
        Assert.DoesNotContain(maliciousSummary, trusted, StringComparison.Ordinal);
        Assert.DoesNotContain(maliciousErrorCode, trusted, StringComparison.Ordinal);
        Assert.DoesNotContain(maliciousContent, trusted, StringComparison.Ordinal);
        Assert.Contains(maliciousSummary, untrusted, StringComparison.Ordinal);
        Assert.Contains(maliciousErrorCode, untrusted, StringComparison.Ordinal);
        Assert.Contains(maliciousContent, untrusted, StringComparison.Ordinal);
        return Complete("Treated successful tool output as untrusted.");
    }

    private static void AssertNoOrphanToolResults(AgentProviderRequest request)
    {
        var callIds = request
            .Turns.SelectMany(turn => turn.Items)
            .Where(item =>
                item.Kind == AgentTurnItemKind.ToolCall && !string.IsNullOrWhiteSpace(item.CallId)
            )
            .Select(item => item.CallId!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (
            var result in request
                .Turns.SelectMany(turn => turn.Items)
                .Where(item => item.Kind == AgentTurnItemKind.ToolResult)
        )
        {
            Assert.True(
                !string.IsNullOrWhiteSpace(result.CallId) && callIds.Contains(result.CallId!),
                $"Tool result '{result.CallId}' was sent without its matching tool call."
            );
        }
    }

    private static void AssertNoUnpairedToolItems(AgentProviderRequest request)
    {
        var items = request.Turns.SelectMany(turn => turn.Items).ToArray();
        var callIds = items
            .Where(item =>
                item.Kind == AgentTurnItemKind.ToolCall && !string.IsNullOrWhiteSpace(item.CallId)
            )
            .Select(item => item.CallId!)
            .ToHashSet(StringComparer.Ordinal);
        var resultIds = items
            .Where(item =>
                item.Kind == AgentTurnItemKind.ToolResult && !string.IsNullOrWhiteSpace(item.CallId)
            )
            .Select(item => item.CallId!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var callId in callIds)
        {
            Assert.True(
                resultIds.Contains(callId),
                $"Tool call '{callId}' was sent without its matching tool result."
            );
        }

        foreach (var resultId in resultIds)
        {
            Assert.True(
                callIds.Contains(resultId),
                $"Tool result '{resultId}' was sent without its matching tool call."
            );
        }
    }

    private static AgentProviderStreamEvent AssertDeniedToolResultAndComplete(
        AgentProviderRequest request,
        string toolId,
        string callId
    )
    {
        Assert.Contains(
            request.Turns,
            turn =>
                turn.Kind == AgentTurnKind.ToolResult
                && turn.Items.Any(item =>
                    item.Kind == AgentTurnItemKind.ToolResult
                    && item.ToolId == toolId
                    && item.CallId == callId
                    && item.TextContent is not null
                    && item.TextContent.Contains(
                        "Permission denied",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
        );
        AssertNoUnpairedToolItems(request);

        return Complete("A calm poem after the denied tool call.");
    }

    private static AgentProviderStreamEvent AssertErroredToolResultAndComplete(
        AgentProviderRequest request,
        string toolId,
        string callId,
        string errorCode,
        string expectedContent = "fatal: not a git repository"
    )
    {
        Assert.Contains(
            request.Turns,
            turn =>
                turn.Kind == AgentTurnKind.ToolResult
                && turn.Items.Any(item =>
                    item.Kind == AgentTurnItemKind.ToolResult
                    && item.ToolId == toolId
                    && item.CallId == callId
                    && item.TextContent is not null
                    && item.TextContent.Contains(expectedContent, StringComparison.Ordinal)
                )
        );
        AssertNoUnpairedToolItems(request);

        return Complete("Interpreted the tool error and continued.");
    }

    private static AgentProviderStreamEvent AssertDuplicateReuseAndComplete(
        AgentProviderRequest request,
        string toolId
    )
    {
        Assert.Contains(
            request.Turns,
            turn =>
                turn.Kind == AgentTurnKind.ToolResult
                && turn.Items.Any(item =>
                    item.Kind == AgentTurnItemKind.ToolResult
                    && item.ToolId == toolId
                    && item.TextContent is not null
                    && item.TextContent.Contains(
                        "Duplicate read-only tool call skipped",
                        StringComparison.Ordinal
                    )
                )
        );

        return Complete("Finished after reusing the cached read-only result.");
    }

    private static AgentProviderStreamEvent AssertCompactedToolResultAndComplete(
        AgentProviderRequest request,
        string toolId
    )
    {
        var toolResult = request.Turns
            .Where(turn => turn.Kind == AgentTurnKind.ToolResult)
            .SelectMany(turn => turn.Items)
            .Single(item => item.Kind == AgentTurnItemKind.ToolResult && item.ToolId == toolId);
        Assert.NotNull(toolResult.TextContent);
        Assert.Contains("chunk-0000", toolResult.TextContent, StringComparison.Ordinal);
        Assert.Contains("[compacted for prompt budget", toolResult.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("chunk-1999", toolResult.TextContent, StringComparison.Ordinal);
        return Complete("Used compacted tool result.");
    }

    private static AgentProviderStreamEvent AssertPermissionResumeContextAndComplete(AgentProviderRequest request)
    {
        var systemInstructions = request.SystemInstructions ?? string.Empty;
        Assert.DoesNotContain("approval-old-000", systemInstructions, StringComparison.Ordinal);
        Assert.Contains(
            request.Turns,
            turn => turn.Role == AgentMessageRole.User
                    && RenderTurnText(turn).Contains("Supplementary user-role context follows as JSON", StringComparison.Ordinal)
                    && RenderTurnText(turn).Contains("approval-old-000", StringComparison.Ordinal));
        Assert.DoesNotContain(request.Turns, turn => RenderTurnText(turn) == "approval-old-000");
        Assert.Contains(
            request.Turns,
            turn => turn.Kind == AgentTurnKind.ToolResult
                    && turn.Items.Any(item => item.CallId == "call-1"));
        return Complete("Used the tool result with projected context.");
    }

    private static bool AssertActiveRunContext(
        AgentProviderRequest request,
        int requestIndex,
        string currentUserMessage
    )
    {
        var activeStartIndex =
            request
                .Turns.Select((turn, index) => new { turn, index })
                .FirstOrDefault(entry =>
                    entry.turn.Role == AgentMessageRole.User
                    && entry.turn.Kind == AgentTurnKind.Message
                    && RenderTurnText(entry.turn) == currentUserMessage
                )
                ?.index ?? -1;

        Assert.True(
            activeStartIndex >= 0,
            "The exact active user turn must remain in the provider request after compaction.");
        AssertNoUnpairedToolItems(request);
        var requestItems = request.Turns.SelectMany(turn => turn.Items).ToArray();
        if (requestIndex > 1)
        {
            var latestCallId = $"call-{requestIndex - 1}";
            Assert.Contains(
                requestItems,
                item => item.Kind == AgentTurnItemKind.ToolCall && item.CallId == latestCallId);
            Assert.Contains(
                requestItems,
                item => item.Kind == AgentTurnItemKind.ToolResult && item.CallId == latestCallId);
        }

        return requestIndex > 2 && requestItems.All(item => item.CallId != "call-1");
    }

    private static AgentProviderStreamEvent AssertToolAvailableAndRequest(
        AgentProviderRequest request,
        string toolId,
        string callId,
        string argumentsJson
    )
    {
        Assert.Contains(
            request.AvailableTools ?? [],
            tool => string.Equals(tool.ToolId, toolId, StringComparison.OrdinalIgnoreCase)
        );
        return ToolRequest(callId, toolId, argumentsJson);
    }

    private static AgentProviderStreamEvent ToolRequest(
        string callId,
        string toolId,
        string argumentsJson
    ) =>
        new(
            AgentProviderStreamEventType.ToolCallRequested,
            ToolCall: new AgentToolCallRequest(callId, toolId, argumentsJson)
        );

    private static AgentProviderStreamEvent ToolRequests(params AgentToolCallRequest[] toolCalls) =>
        new(AgentProviderStreamEventType.ToolCallRequested, ToolCalls: toolCalls);

    private static AgentProviderStreamEvent Delta(string delta) =>
        new(AgentProviderStreamEventType.TextDelta, Delta: delta);

    private static AgentProviderStreamEvent ReasoningDelta(string delta) =>
        new(AgentProviderStreamEventType.ReasoningDelta, Delta: delta);

    private static AgentProviderStreamEvent Complete(string content) =>
        new(AgentProviderStreamEventType.Completed, Response: new AgentProviderResponse(content));

    private static UsageDetails Usage(long totalTokens)
        => new() { TotalTokenCount = totalTokens };

    private static AgentProviderStreamEvent ThrowExecutionFailure(string message) =>
        throw new InvalidOperationException(message);

    private static AgentProviderStreamEvent TransientStreamError() =>
        new(
            AgentProviderStreamEventType.Error,
            Response: new AgentProviderResponse(
                "The response ended prematurely. (ResponseEnded)",
                IsError: true,
                ErrorCode: "ResponseEnded"
            )
        );

    private static AgentProviderStreamEvent TerminalStreamError() =>
        new(
            AgentProviderStreamEventType.Error,
            Response: new AgentProviderResponse(
                "The provider rejected the request.",
                IsError: true,
                ErrorCode: "invalid-request"));

    private static AgentProviderStreamEvent ContextWindowError() =>
        new(
            AgentProviderStreamEventType.Error,
            Response: new AgentProviderResponse(
                "The selected model rejected the request because its context window was exceeded.",
                IsError: true,
                ErrorCode: "test-context-window-exceeded",
                FailureKind: AgentChatProviderFailureKind.ContextWindowExceeded));

    private static bool RequestContainsUserText(AgentProviderRequest request, string text) =>
        request.Turns.Any(turn =>
            turn.Role == AgentMessageRole.User
            && RenderTurnText(turn).Contains(text, StringComparison.Ordinal)
        );

    private static string RenderTurnText(AgentTurnRecord turn) =>
        string.Join(
            "\n\n",
            turn.Items.Where(item =>
                    item.Kind == AgentTurnItemKind.Text
                    && !string.IsNullOrWhiteSpace(item.TextContent)
                )
                .Select(item => item.TextContent!.Trim())
        );

    private static IReadOnlyList<AgentDurableRunStatus> ListDurableRunStatuses(
        AgentTestRuntime runtime,
        Guid sessionId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = runtime.Store.DatabasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Status FROM AgentRuns WHERE SessionId = $sessionId ORDER BY RunRevision;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString());
        using var reader = command.ExecuteReader();
        var statuses = new List<AgentDurableRunStatus>();
        while (reader.Read())
        {
            statuses.Add(Enum.Parse<AgentDurableRunStatus>(reader.GetString(0), ignoreCase: true));
        }

        return statuses;
    }

    private static async Task<McpSettingsTestScope> CreateMcpSettingsViewModel(
        McpServerCatalogService catalog,
        McpClientConnectionManager connections)
    {
        var configuration = new McpConfigurationCoordinator(new McpEcosystemConfigurationImporter(catalog), syncService: null);
        var service = new McpManagementService(catalog, new McpSettingsEditorService(catalog), configuration,
            new McpServerConnectionService(catalog, connections, oauthService: null),
            new McpOAuthCoordinator(oauthService: null, connections), new TestPackageContext(Path.GetTempPath()));
        var extensions = new RegressionTestExtensionCatalog();
        extensions.AddGeneratedProvider("sunder.package.agent.mcp", service, service.CreateSunderRpcProviderRegistration());
        var receiver = new GeneratedServiceTestScope(extensions.RpcForCaller("sunder.package.agent.mcp"));
        var selected = Assert.Single((await receiver.Providers<McpAppManagementService>().ListAsync()).Providers);
        var gateway = new McpAppRuntimeGateway(selected.Service, NullPackageCallbackClient.Instance);
        return new(new AgentMcpSettingsViewModel(gateway), gateway, receiver, configuration, extensions);
    }

    private sealed record McpSettingsTestScope(AgentMcpSettingsViewModel ViewModel, McpAppRuntimeGateway Gateway,
        GeneratedServiceTestScope Receiver, McpConfigurationCoordinator Configuration, RegressionTestExtensionCatalog Extensions) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            ViewModel.Dispose();
            await Gateway.DisposeAsync();
            await Receiver.DisposeAsync();
            await Configuration.DisposeAsync();
            Extensions.Dispose();
        }
    }

    private static ConfiguredMcpServerRecord CreateMcpServer(
        string serverId,
        string name,
        string displayName
    ) =>
        new()
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
        while (!EvaluateWaitCondition(condition))
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeoutCts.Token);
        }
    }

    private static bool EvaluateWaitCondition(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch (InvalidOperationException exception) when (
            exception.Message.StartsWith("Collection was modified", StringComparison.Ordinal))
        {
            return false;
        }
    }

    private sealed class InlineAgentPresentationDispatcher : AgentPresentationDispatcher,
        SubagentsApp::Sunder.Package.Agent.Shared.Presentation.IPresentationDispatcher
    {
        private readonly object _gate = new();

        public static InlineAgentPresentationDispatcher Instance { get; } = new();

        public bool CheckAccess() => Monitor.IsEntered(_gate);

        public Task InvokeAsync(Action action)
        {
            lock (_gate)
            {
                action();
            }
            return Task.CompletedTask;
        }
    }

    private static bool ContainsSubsessionTextRow(SubsessionsViewModel viewModel, string content)
    {
        try
        {
            return viewModel.Messages
                .OfType<SubsessionTextTranscriptRowViewModel>()
                .Any(row => row.Content == content);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string CreateTempTestRoot()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-agent-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(rootPath);
        return rootPath;
    }

    private static T InvokePrivateStatic<T>(Type type, string methodName, object?[] parameters)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<T>(method.Invoke(null, parameters));
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

    private static void AddSubagentBehaviorLoop(TestExtensionCatalog extensionCatalog)
    {
        extensionCatalog.AddBehaviorLoop(new OrchestratedAgentBehaviorLoop()
        );
    }

    private static void SaveProfileBehaviorLoop(
        AgentProfileService profileService,
        AgentProfileRecord profile,
        string behaviorLoopId
    )
    {
        profileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            profile.ChatProviderId,
            profile.ChatModelId,
            profile.EmbeddingProviderId,
            profile.EmbeddingModelId,
            selectableCapabilityAssignments: profile.SelectableCapabilityAssignments,
            behaviorLoopId: behaviorLoopId,
            behaviorLoopSourceId: profile.BehaviorLoopSourceId,
            behaviorLoopSettingsJson: profile.BehaviorLoopSettingsJson
        );
    }

    private static AgentProfileRecord CreateBehaviorLoopProfile(string behaviorLoopId)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentProfileRecord(
            "profile-1",
            "Profile",
            null,
            null,
            "provider",
            "model",
            null,
            null,
            now,
            now,
            [],
            [],
            behaviorLoopId
        );
    }

    private static SubagentRecord SaveSubagent(SubagentService service, string subagentId, string displayName,
        string? description, string? instructions, string? chatProviderId, string? chatModelId,
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? selectableCapabilityAssignments,
        string? chatModelSettingsJson = null)
        => service.SaveSubagent(subagentId, displayName, description, instructions, chatProviderId, chatModelId,
             selectableCapabilityAssignments?.Select(assignment =>
                 new SubagentsWorker::Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentProfileSelectableCapabilityAssignmentRecord
                 {
                     Kind = assignment.Kind,
                     CapabilityId = assignment.CapabilityId,
                     SourceId = assignment.SourceId,
                 }).ToArray(),
             chatModelSettingsJson);

    private static AppModels.AgentTurnRecord ToAppTurn(AgentTurnRecord turn)
        => new(
            turn.TurnId,
            turn.SessionId,
            turn.Role switch
            {
                AgentMessageRole.System => AppModels.AgentMessageRole.System,
                AgentMessageRole.User => AppModels.AgentMessageRole.User,
                AgentMessageRole.Assistant => AppModels.AgentMessageRole.Assistant,
                AgentMessageRole.Tool => AppModels.AgentMessageRole.Tool,
                _ => throw new ArgumentOutOfRangeException(nameof(turn)),
            },
            turn.Kind switch
            {
                AgentTurnKind.Message => AppModels.AgentTurnKind.Message,
                AgentTurnKind.ToolCall => AppModels.AgentTurnKind.ToolCall,
                AgentTurnKind.ToolResult => AppModels.AgentTurnKind.ToolResult,
                _ => throw new ArgumentOutOfRangeException(nameof(turn)),
            },
            turn.Items.Select(item => new AppModels.AgentTurnItemRecord(
                item.ItemId,
                item.TurnId,
                item.SequenceNumber,
                item.Kind switch
                {
                    AgentTurnItemKind.Text => AppModels.AgentTurnItemKind.Text,
                    AgentTurnItemKind.ToolCall => AppModels.AgentTurnItemKind.ToolCall,
                    AgentTurnItemKind.ToolResult => AppModels.AgentTurnItemKind.ToolResult,
                    AgentTurnItemKind.Attachment => AppModels.AgentTurnItemKind.Attachment,
                    _ => throw new ArgumentOutOfRangeException(nameof(item)),
                },
                item.TextContent,
                item.CallId,
                item.ToolId,
                item.ArgumentsJson,
                item.ResultSummary,
                item.StructuredPayloadJson,
                item.SourcesJson,
                item.WasTruncated,
                item.IsError,
                item.ErrorCode,
                item.BackendId,
                PresentationPayloadJson: item.PresentationPayloadJson
            )).ToArray(),
            turn.CreatedAtUtc,
            turn.UpdatedAtUtc)
        {
            ContentRevision = turn.ContentRevision,
            IsStreaming = turn.IsStreaming,
            RunId = turn.RunId,
            RunRevision = turn.RunRevision,
        };

    private sealed class BlockingTranscriptAnchorGateway(AgentTurnRecord turn) :
        AppRuntime.IAgentTranscriptAnchorGateway
    {
        internal TaskCompletionSource LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseLoad { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AppRuntime.AgentTranscriptAroundTurnPage> LoadTranscriptAroundTurnAsync(
            AppRuntime.AgentTranscriptAroundTurnRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(turn.SessionId, request.SessionId);
            Assert.Equal(turn.TurnId, request.TurnId);
            LoadStarted.TrySetResult();
            await ReleaseLoad.Task;
            return new AppRuntime.AgentTranscriptAroundTurnPage(
                1,
                [ToAppTurn(turn)],
                HasOlder: false,
                HasNewer: false,
                turn.TurnId);
        }
    }

    private sealed class ThrowingRunGateway :
        IAgentRunGateway,
        IAgentCorrelatedRunGateway,
        IAgentRunCommandStatusGateway
    {
        public static ThrowingRunGateway Instance { get; } = new();

        public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(
            Guid sessionId,
            string profileId,
            string userMessage,
            string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default)
            => Task.FromException<AgentRunCheckpointRecord>(
                new InvalidOperationException("Injected run rejection."));

        public Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(
            Guid sessionId,
            Guid rollbackAnchorTurnId,
            string profileId,
            string userMessage,
            string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default)
            => QueueUserMessageAsync(
                sessionId,
                profileId,
                userMessage,
                workspaceId,
                attachments,
                cancellationToken);

        public Task<AgentRunCheckpointRecord?> StopAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);

        public Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(
            Guid sessionId,
            string requestId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);

        public Task<AgentRunCheckpointRecord?> DenyPendingPermissionAsync(
            Guid sessionId,
            string requestId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);

        Task<AgentRunCheckpointRecord> IAgentCorrelatedRunGateway.QueueUserMessageAsync(
            Guid sessionId,
            string profileId,
            string userMessage,
            string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            Guid userTurnId,
            CancellationToken cancellationToken)
            => QueueUserMessageAsync(
                sessionId,
                profileId,
                userMessage,
                workspaceId,
                attachments,
                cancellationToken);

        Task<AgentRunCheckpointRecord> IAgentCorrelatedRunGateway.RollbackAndQueueUserMessageAsync(
            Guid sessionId,
            Guid rollbackAnchorTurnId,
            string profileId,
            string userMessage,
            string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            Guid userTurnId,
            CancellationToken cancellationToken)
            => RollbackAndQueueUserMessageAsync(
                sessionId,
                rollbackAnchorTurnId,
                profileId,
                userMessage,
                workspaceId,
                attachments,
                cancellationToken);

        public Task<AgentRunCommandStatus> GetRunCommandStatusAsync(
            Guid sessionId,
            Guid userTurnId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(AgentRunCommandStatus.Absent);
    }

    private sealed class PendingAdmissionRunGateway(AgentLocalStore store) :
        IAgentRunGateway,
        IAgentCorrelatedRunGateway,
        IAgentRunCommandStatusGateway
    {
        private AgentUserTurnAdmissionRequest? _pending;
        private int _commandCount;

        internal TaskCompletionSource StatusChecked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int CommandCount => Volatile.Read(ref _commandCount);

        internal Guid? SessionId => _pending?.SessionId;

        public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(
            Guid sessionId,
            string profileId,
            string userMessage,
            string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default)
            => Task.FromException<AgentRunCheckpointRecord>(new NotSupportedException());

        public Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(
            Guid sessionId,
            Guid rollbackAnchorTurnId,
            string profileId,
            string userMessage,
            string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default)
            => Task.FromException<AgentRunCheckpointRecord>(new NotSupportedException());

        Task<AgentRunCheckpointRecord> IAgentCorrelatedRunGateway.QueueUserMessageAsync(
            Guid sessionId,
            string profileId,
            string userMessage,
            string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            Guid userTurnId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Empty(attachments);
            Interlocked.Increment(ref _commandCount);
            var fingerprint = AgentUserTurnRequestFingerprint.Compute(
                sessionId,
                profileId,
                workspaceId,
                userMessage,
                AgentRunAdmissionKind.Normal,
                rollbackAnchorTurnId: null,
                []);
            _pending = new AgentUserTurnAdmissionRequest(
                userTurnId,
                sessionId,
                profileId,
                workspaceId,
                userMessage,
                AgentRunAdmissionKind.Normal,
                RollbackAnchorTurnId: null,
                fingerprint,
                []);
            return Task.FromException<AgentRunCheckpointRecord>(
                new InvalidOperationException("The Runtime response was lost while admission remained pending."));
        }

        Task<AgentRunCheckpointRecord> IAgentCorrelatedRunGateway.RollbackAndQueueUserMessageAsync(
            Guid sessionId,
            Guid rollbackAnchorTurnId,
            string profileId,
            string userMessage,
            string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            Guid userTurnId,
            CancellationToken cancellationToken)
            => Task.FromException<AgentRunCheckpointRecord>(new NotSupportedException());

        public Task<AgentRunCommandStatus> GetRunCommandStatusAsync(
            Guid sessionId,
            Guid userTurnId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatusChecked.TrySetResult();
            var run = store.GetRunByUserTurnId(userTurnId);
            return Task.FromResult(run?.Key.SessionId == sessionId
                ? AgentRunCommandStatus.Committed
                : AgentRunCommandStatus.Pending);
        }

        public Task<AgentRunCheckpointRecord?> StopAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);

        public Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(
            Guid sessionId,
            string requestId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);

        public Task<AgentRunCheckpointRecord?> DenyPendingPermissionAsync(
            Guid sessionId,
            string requestId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);

        internal void CommitPendingAdmission()
            => store.AdmitUserTurn(Assert.IsType<AgentUserTurnAdmissionRequest>(_pending));
    }

    private sealed class FailOnceTranscriptPageGateway(AgentSessionService sessions)
        : AppRuntime.IAgentTranscriptPageGateway
    {
        private int _invocationCount;
        private int _failNextTurnLookup = 1;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public Task<AppRuntime.AgentTranscriptPage> LoadTranscriptPageAsync(
            AppRuntime.AgentTranscriptPageRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _invocationCount);
            if (request.Direction == AppRuntime.AgentTranscriptPageDirection.Turn
                && Interlocked.Exchange(ref _failNextTurnLookup, 0) != 0)
            {
                throw new InvalidOperationException("Injected reconciliation failure.");
            }

            IReadOnlyList<AgentTurnRecord> turns = request.Direction switch
            {
                AppRuntime.AgentTranscriptPageDirection.Turn when request.AnchorTurnId is { } turnId
                    => sessions.GetTurn(turnId) is { } turn ? [turn] : [],
                AppRuntime.AgentTranscriptPageDirection.Recent
                    => sessions.ListRecentTurns(request.SessionId, request.Limit),
                _ => [],
            };
            return Task.FromResult(new AppRuntime.AgentTranscriptPage(0, turns.Select(ToAppTurn).ToArray(), false));
        }
    }

    private sealed class AgentTestRuntime : IDisposable
    {
        private readonly string _rootPath;
        private readonly TestExtensionCatalog _extensionCatalog;

        private AgentTestRuntime(
            string rootPath,
            TestExtensionCatalog extensionCatalog,
            AgentLocalStore store,
            AgentActiveRunRegistry activeRunRegistry,
            AgentRunCoordinator runCoordinator,
            AgentMemoryCoordinator memoryCoordinator,
            AgentToolService toolService,
            AgentSessionService sessionService,
            AgentWorkspaceService workspaceService,
            AgentPermissionService permissionService,
            AgentProfileService profileService,
            AgentAttachmentService attachmentService,
            AgentParentRunContinuationService parentRunContinuationService,
            AgentBackgroundWorkService backgroundWork,
            AgentRunDispatcher dispatcher
        )
        {
            _rootPath = rootPath;
            _extensionCatalog = extensionCatalog;
            Store = store;
            ActiveRunRegistry = activeRunRegistry;
            RunCoordinator = runCoordinator;
            MemoryCoordinator = memoryCoordinator;
            ToolService = toolService;
            SessionService = sessionService;
            WorkspaceService = workspaceService;
            PermissionService = permissionService;
            ProfileService = profileService;
            AttachmentService = attachmentService;
            ParentRunContinuationService = parentRunContinuationService;
            BackgroundWork = backgroundWork;
            Dispatcher = dispatcher;
        }

        public AgentRunCoordinator RunCoordinator { get; }

        public AgentLocalStore Store { get; }

        public AgentActiveRunRegistry ActiveRunRegistry { get; }

        public AgentMemoryCoordinator MemoryCoordinator { get; }

        public AgentToolService ToolService { get; }

        public AgentSessionService SessionService { get; }

        public AgentWorkspaceService WorkspaceService { get; }

        public AgentPermissionService PermissionService { get; }

        public AgentProfileService ProfileService { get; }

        public AgentAttachmentService AttachmentService { get; }

        public AgentParentRunContinuationService ParentRunContinuationService { get; }

        public AgentBackgroundWorkService BackgroundWork { get; }

        public AgentRunDispatcher Dispatcher { get; }

        public TestExtensionCatalog ExtensionCatalog => _extensionCatalog;

        public string RootPath => _rootPath;

        public string CurrentProfileId { get; private set; } = string.Empty;

        public string CurrentWorkspaceId { get; private set; } = string.Empty;

        public AgentProfileRecord CurrentProfile => ProfileService.GetProfile(CurrentProfileId)!;

        public static AgentTestRuntime Create(
            object provider,
            params IAgentTool[] tools
        ) => CreateCore(provider, budgetLimits: null, tools: tools);

        public static AgentTestRuntime CreateWithBudget(
            object provider,
            AgentRunBudgetLimits budgetLimits,
            params IAgentTool[] tools
        ) => CreateCore(provider, budgetLimits, tools: tools);

        public static AgentTestRuntime CreateWithContinuityRefinement(
            object provider,
            params IAgentTool[] tools
        ) => CreateCore(provider, budgetLimits: null, useContinuityRefinement: true, tools: tools);

        public static AgentTestRuntime CreateWithoutChatProvider(params IAgentTool[] tools) =>
            CreateCore(provider: null, budgetLimits: null, tools: tools);

        private static AgentTestRuntime CreateCore(
            object? provider,
            AgentRunBudgetLimits? budgetLimits,
            bool useContinuityRefinement = false,
            params IAgentTool[] tools
        )
        {
            var temporaryRoot = OperatingSystem.IsMacOS()
                ? "/private" + Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
                : Path.GetTempPath();
            var rootPath = Path.Combine(
                temporaryRoot,
                "sunder-agent-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(rootPath);

            var extensionCatalog = new TestExtensionCatalog();
            if (provider is not null)
            {
                extensionCatalog.AddProvider(
                    RegressionTestBindings.ChatProviders,
                    provider,
                    AgentRoleBoundary.As<IAgentChatProvider>(provider).Descriptor.PackageId ?? "test.package");
            }

            foreach (var tool in tools)
            {
                extensionCatalog.AddTool(tool);
            }

            var packageContext = new TestPackageContext(rootPath);
            var store = new AgentLocalStore(packageContext);
            var sessionService = new AgentSessionService(store, extensionCatalog.SessionCleaners);
            var workspaceService = new AgentWorkspaceService(store, extensionCatalog.SessionCleaners, sessionService, extensionCatalog.WorkspacePathMigrators);
            var permissionService = new AgentPermissionService(store, extensionCatalog.PermissionSurfaces);
            var executionTargetService = new AgentExecutionTargetService(extensionCatalog.ExecutionTargets);
            var toolService = new AgentToolService(
                sessionService,
                workspaceService,
                executionTargetService,
                extensionCatalog.ToolSources,
                extensionCatalog.ObservedToolExecutions
            );
            var profileService = extensionCatalog.CreateProfileService(store, toolService);
            var providerResolver = new AgentRunProviderResolver(profileService, extensionCatalog.ChatProviders);
            extensionCatalog.AddProvider(
                RegressionTestBindings.RuntimeCatalog,
                new AgentRuntimeCatalog(sessionService, profileService, workspaceService),
                "sunder.package.agent"
            );
            var memoryCoordinator = new AgentMemoryCoordinator(
                sessionService,
                extensionCatalog.PromptContextContributors,
                new AgentLifecycleDispatcher(store, extensionCatalog.DurableLifecycleObservers),
                executionTargetService: executionTargetService);
            var sessionContextProjectionService = useContinuityRefinement
                ? new AgentSessionContextProjectionService(
                    sessionService,
                    new AgentSessionContinuityGenerationService(providerResolver))
                : new AgentSessionContextProjectionService(sessionService);
            var promptComposer = new AgentSystemPromptComposer(extensionCatalog.SystemPromptContributors);
            var attachmentService = new AgentAttachmentService(packageContext);
            extensionCatalog.AddProvider(
                RegressionTestBindings.SessionCleaners,
                attachmentService
            );
            var terminalHandler = new AgentLoopTerminalHandler();
            var defaultBehaviorLoop = new DefaultAgentBehaviorLoop(
                new AgentPromptPreparationPipeline(
                    promptComposer,
                    attachmentService,
                    sessionContextProjectionService),
                new AgentProviderCycleRunner(new AgentStreamingTurnWriter(terminalHandler)),
                new AgentToolCycleCoordinator(terminalHandler),
                terminalHandler,
                budgetLimits);
            extensionCatalog.AddBehaviorLoop(defaultBehaviorLoop, "sunder.package.agent");
            var runAttachmentStore = new AgentRunAttachmentStore(attachmentService);
            var activeRunRegistry = extensionCatalog.ActiveRuns;
            var runEventLogger = new AgentRunEventLogger(packageContext);
            var backgroundWork = new AgentBackgroundWorkService();
            backgroundWork.StartAsync().GetAwaiter().GetResult();
            var sessionTitleService = new AgentSessionTitleService(
                sessionService,
                providerResolver,
                runEventLogger,
                backgroundWork
            );
            var behaviorLoopResolver = new AgentBehaviorLoopResolver(
                extensionCatalog.BehaviorLoops,
                extensionCatalog.RunControls,
                activeRunRegistry,
                defaultBehaviorLoop
            );
            var stopCoordinator = new AgentRunStopCoordinator(
                sessionService,
                permissionService,
                memoryCoordinator,
                activeRunRegistry,
                profileService
            );
            var behaviorLoopHostFactory = new AgentBehaviorLoopHostFactory(
                sessionService,
                toolService,
                permissionService,
                memoryCoordinator,
                runEventLogger,
                activeRunRegistry,
                defaultBehaviorLoop
            );
            var runPreparationService = new AgentRunPreparationService(
                sessionService,
                profileService,
                workspaceService,
                runAttachmentStore,
                runEventLogger,
                providerResolver,
                sessionTitleService
            );
            var runStartService = new AgentRunStartService(
                sessionService,
                activeRunRegistry,
                runEventLogger,
                sessionTitleService
            );
            var runExecutionService = new AgentRunExecutionService(
                sessionService,
                workspaceService,
                memoryCoordinator,
                activeRunRegistry,
                runEventLogger,
                behaviorLoopHostFactory,
                behaviorLoopResolver
            );
            var childRunSessionService = new AgentChildRunSessionService(
                sessionService,
                profileService
            );
            var parentRunContinuationService = new AgentParentRunContinuationService(
                sessionService,
                profileService,
                workspaceService,
                providerResolver,
                activeRunRegistry,
                behaviorLoopHostFactory,
                behaviorLoopResolver,
                childRunSessionService,
                backgroundWork: backgroundWork
            );
            var permissionResumeCoordinator = new AgentPermissionResumeCoordinator(
                sessionService,
                workspaceService,
                profileService,
                permissionService,
                providerResolver,
                activeRunRegistry,
                runEventLogger,
                behaviorLoopHostFactory,
                behaviorLoopResolver,
                parentRunContinuationService,
                backgroundWork: backgroundWork
            );
            var admissionService = new AgentUserTurnAdmissionService(
                sessionService,
                runAttachmentStore,
                activeRunRegistry);
            var dispatcher = new AgentRunDispatcher(
                sessionService,
                runPreparationService,
                runStartService,
                runExecutionService,
                activeRunRegistry);
            var userMessageRunCoordinator = new AgentUserMessageRunCoordinator(
                sessionService,
                runPreparationService,
                runStartService,
                runExecutionService,
                activeRunRegistry,
                admissionService: admissionService,
                dispatcher: dispatcher
            );
            var runCoordinator = new AgentRunCoordinator(
                userMessageRunCoordinator,
                stopCoordinator,
                new AgentChildRunExecutor(userMessageRunCoordinator, childRunSessionService),
                permissionResumeCoordinator
            );

            return new AgentTestRuntime(
                rootPath,
                extensionCatalog,
                store,
                activeRunRegistry,
                runCoordinator,
                memoryCoordinator,
                toolService,
                sessionService,
                workspaceService,
                permissionService,
                profileService,
                attachmentService,
                parentRunContinuationService,
                backgroundWork,
                dispatcher
            );
        }

        public void AddMemoryFeature(CapturingMemoryFeature feature)
        {
            _extensionCatalog.AddProvider(
                RegressionTestBindings.PromptContextContributors,
                feature
            );
            _extensionCatalog.AddProvider(RegressionTestBindings.DurableLifecycleObservers, feature);
        }

        public void AddEmbeddingProvider(object provider) =>
            _extensionCatalog.AddProvider(RegressionTestBindings.EmbeddingProviders, provider);

        public async Task<Guid> CreateSessionAsync(params string[] toolIds)
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
                selectableCapabilityAssignments: toolIds
                    .Select(toolId => new AgentProfileSelectableCapabilityAssignmentRecord(
                        AgentProfileSelectableCapabilityKinds.Tool,
                        toolId))
                    .ToArray()
            );

            CurrentProfileId = profile.ProfileId;
            var workspace = WorkspaceService.CreateWorkspace("Test Workspace");
            CurrentWorkspaceId = workspace.WorkspaceId;
            return SessionService.CreateSession("Test Session", workspaceId: workspace.WorkspaceId).SessionId;
        }

        public void Dispose()
        {
            try
            {
                try
                {
                    Dispatcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    BackgroundWork.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    _extensionCatalog.Dispose();
                    ProfileService.Dispose();
                    Store.Dispose();
                }
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

    private sealed class ScriptedProvider : IAgentChatProvider, IAgentUtilityModelProvider
    {
        private readonly Func<
            AgentProviderRequest,
            int,
            IReadOnlyList<AgentProviderStreamEvent>
        > _handler;
        private readonly bool _supportsMultipleToolCalls;
        private readonly bool _supportsImageInput;
        private readonly IReadOnlyList<AgentModelDescriptor> _models;
        private readonly AgentProviderReadinessStatus _readinessStatus;
        private readonly string _readinessMessage;
        private readonly string? _utilityModelId;
        private readonly Func<
            CancellationToken,
            ValueTask<AgentProviderReadiness>
        >? _readinessHandler;
        private readonly Func<CancellationToken, ValueTask>? _beforeExecutionHandler;

        public ScriptedProvider(
            Func<AgentProviderRequest, int, AgentProviderStreamEvent> handler,
            bool supportsMultipleToolCalls = false,
            IReadOnlyList<AgentModelDescriptor>? models = null,
            AgentProviderReadinessStatus readinessStatus = AgentProviderReadinessStatus.Ready,
            string readinessMessage = "Ready.",
            string? packageId = "test.package",
            string? utilityModelId = null,
            Func<CancellationToken, ValueTask<AgentProviderReadiness>>? readinessHandler = null,
            Func<CancellationToken, ValueTask>? beforeExecutionHandler = null,
            bool supportsImageInput = false
        )
            : this(
                (request, requestIndex) => [handler(request, requestIndex)],
                supportsMultipleToolCalls,
                models,
                readinessStatus,
                readinessMessage,
                packageId,
                utilityModelId,
                readinessHandler,
                beforeExecutionHandler,
                supportsImageInput
            )
        { }

        public ScriptedProvider(
            Func<AgentProviderRequest, int, IReadOnlyList<AgentProviderStreamEvent>> handler,
            bool supportsMultipleToolCalls = false,
            IReadOnlyList<AgentModelDescriptor>? models = null,
            AgentProviderReadinessStatus readinessStatus = AgentProviderReadinessStatus.Ready,
            string readinessMessage = "Ready.",
            string? packageId = "test.package",
            string? utilityModelId = null,
            Func<CancellationToken, ValueTask<AgentProviderReadiness>>? readinessHandler = null,
            Func<CancellationToken, ValueTask>? beforeExecutionHandler = null,
            bool supportsImageInput = false
        )
        {
            _handler = handler;
            _supportsMultipleToolCalls = supportsMultipleToolCalls;
            _supportsImageInput = supportsImageInput;
            _models =
                models
                ??
                [
                    new AgentModelDescriptor(
                        "test-model",
                        "Test Model",
                        128_000,
                        4_096,
                        IsRecommended: true
                    ),
                ];
            _readinessStatus = readinessStatus;
            _readinessMessage = readinessMessage;
            _utilityModelId = utilityModelId;
            _readinessHandler = readinessHandler;
            _beforeExecutionHandler = beforeExecutionHandler;
            Descriptor = new AgentProviderDescriptor(
                "test-provider",
                "Test Provider",
                [],
                SupportsStreaming: true,
                SupportsInterruptibleRuns: true
            )
            {
                PackageId = packageId,
            };
        }

        public AgentProviderDescriptor Descriptor { get; }

        public List<AgentProviderRequest> Requests { get; } = [];

        public List<ChatOptions> RequestOptions { get; } = [];

        public List<long> DataContentBytesByRequest { get; } = [];

        public Func<AgentProviderRequest, int, UsageDetails?>? UsageHandler { get; set; }

        public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(_models);

        public ValueTask<string?> ResolveUtilityModelIdAsync(
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(_utilityModelId);

        public ValueTask<AgentProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default
        ) => _readinessHandler is not null
            ? _readinessHandler(cancellationToken)
            : ValueTask.FromResult(
                new AgentProviderReadiness(
                    Descriptor.ProviderId,
                    _readinessStatus,
                    _readinessMessage
                )
            );

        public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(
            string? modelId,
            CancellationToken cancellationToken = default
        ) =>
            ValueTask.FromResult(
                new AgentProviderRunCapabilities(
                    SupportsNativeToolCalling: true,
                    SupportsStreamingToolCalls: true,
                    SupportsMultipleToolCalls: _supportsMultipleToolCalls,
                    Summary: "Test provider supports native tool calling.",
                    SupportsImageInput: _supportsImageInput
                )
            );

        public ValueTask<IChatClient> CreateChatClientAsync(
            AgentChatClientContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IChatClient>(
                new ScriptedChatClient(context, Descriptor.DisplayName, this)
            );
        }

        private static AgentProviderRequest CloneRequest(AgentProviderRequest request) =>
            new(
                request.ProviderId,
                request.ModelId,
                request.SystemInstructions,
                request.Turns.Select(CloneTurn).ToArray(),
                request.AvailableTools?.ToArray(),
                request.ReasoningEffort,
                request.ReasoningOutput
            );

        private static AgentTurnRecord CloneTurn(AgentTurnRecord turn) =>
            new(
                turn.TurnId,
                turn.SessionId,
                turn.Role,
                turn.Kind,
                turn.Items.Select(item => item with { }).ToArray(),
                turn.CreatedAtUtc,
                turn.UpdatedAtUtc
            );

        private sealed class ScriptedChatClient(
            AgentChatClientContext context,
            string providerDisplayName,
            ScriptedProvider provider
        ) : IChatClient
        {
            private readonly AgentChatClientContext _context = context;
            private readonly ScriptedProvider _provider = provider;

            public ChatClientMetadata Metadata { get; } = new(providerDisplayName);

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default
            )
            {
                var responseMessage = new ChatMessage(ChatRole.Assistant, []);
                await foreach (
                    var update in GetStreamingResponseAsync(messages, options, cancellationToken)
                )
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
                [EnumeratorCancellation] CancellationToken cancellationToken = default
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requestMessages = messages.ToArray();
                var dataContentBytes = requestMessages
                    .SelectMany(message => message.Contents)
                    .OfType<DataContent>()
                    .Sum(content => (long)content.Data.Length);
                var request = BuildProviderRequest(_context, requestMessages, options);
                var capturedRequest = CloneRequest(request);
                int requestIndex;
                lock (_provider.Requests)
                {
                    _provider.Requests.Add(capturedRequest);
                    _provider.DataContentBytesByRequest.Add(dataContentBytes);
                    if (options is not null)
                    {
                        _provider.RequestOptions.Add(options);
                    }

                    requestIndex = _provider.Requests.Count;
                }
                if (_provider._beforeExecutionHandler is not null)
                {
                    await _provider._beforeExecutionHandler(cancellationToken);
                }

                var responseId = Guid.NewGuid().ToString("N");
                var messageId = responseId;
                var modelId = options?.ModelId ?? _context.ModelId;
                foreach (var streamEvent in _provider._handler(capturedRequest, requestIndex))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (streamEvent.Type)
                    {
                        case AgentProviderStreamEventType.TextDelta
                            when streamEvent.Delta is not null:
                            yield return new ChatResponseUpdate(
                                ChatRole.Assistant,
                                streamEvent.Delta
                            )
                            {
                                ResponseId = responseId,
                                MessageId = messageId,
                                ModelId = modelId,
                            };
                            break;

                        case AgentProviderStreamEventType.ReasoningDelta
                            when streamEvent.Delta is not null:
                            yield return new ChatResponseUpdate(
                                ChatRole.Assistant,
                                [new TextReasoningContent(streamEvent.Delta)]
                            )
                            {
                                ResponseId = responseId,
                                MessageId = messageId,
                                ModelId = modelId,
                            };
                            break;

                        case AgentProviderStreamEventType.ToolCallRequested
                            when streamEvent.ToolCalls is { Count: > 0 }:
                            yield return CreateToolCallUpdate(
                                streamEvent.ToolCalls,
                                responseId,
                                messageId,
                                modelId
                            );
                            if (CreateUsageUpdate(capturedRequest, requestIndex, responseId, messageId, modelId) is { } batchUsage)
                            {
                                yield return batchUsage;
                            }
                            yield break;

                        case AgentProviderStreamEventType.ToolCallRequested
                            when streamEvent.ToolCall is not null:
                            yield return CreateToolCallUpdate(
                                streamEvent.ToolCall,
                                responseId,
                                messageId,
                                modelId
                            );
                            if (CreateUsageUpdate(capturedRequest, requestIndex, responseId, messageId, modelId) is { } toolUsage)
                            {
                                yield return toolUsage;
                            }
                            yield break;

                        case AgentProviderStreamEventType.Completed
                            when streamEvent.Response is not null:
                            if (!string.IsNullOrWhiteSpace(streamEvent.Response.Content))
                            {
                                yield return new ChatResponseUpdate(
                                    ChatRole.Assistant,
                                    streamEvent.Response.Content
                                )
                                {
                                    ResponseId = responseId,
                                    MessageId = messageId,
                                    ModelId = modelId,
                                };
                            }
                            break;

                        case AgentProviderStreamEventType.Error
                            when streamEvent.Response is not null:
                            throw new AgentChatProviderException(
                                streamEvent.Response.ErrorCode ?? "Provider response failed.",
                                streamEvent.Response.Content,
                                streamEvent.Response.ErrorCode
                            )
                            {
                                FailureKind = streamEvent.Response.FailureKind,
                            };
                    }
                }

                if (CreateUsageUpdate(capturedRequest, requestIndex, responseId, messageId, modelId) is { } usage)
                {
                    yield return usage;
                }

                await Task.CompletedTask;
            }

            public object? GetService(Type serviceType, object? serviceKey = null) =>
                serviceKey is null && serviceType.IsInstanceOfType(this) ? this
                : serviceKey is null && serviceType == typeof(ChatClientMetadata) ? Metadata
                : null;

            public void Dispose() { }

            private ChatResponseUpdate? CreateUsageUpdate(
                AgentProviderRequest request,
                int requestIndex,
                string responseId,
                string messageId,
                string? modelId)
            {
                var usage = _provider.UsageHandler?.Invoke(request, requestIndex);
                return usage is null
                    ? null
                    : new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)])
                    {
                        ResponseId = responseId,
                        MessageId = messageId,
                        ModelId = modelId,
                    };
            }

            private static AgentProviderRequest BuildProviderRequest(
                AgentChatClientContext context,
                IEnumerable<ChatMessage> messages,
                ChatOptions? options
            ) =>
                new(
                    context.ProviderId,
                    options?.ModelId ?? context.ModelId,
                    options?.Instructions,
                    BuildProviderTurns(messages, options?.ConversationId),
                    options?.ToolMode == ChatToolMode.None
                        ? null
                        : BuildProviderToolDescriptors(options?.Tools),
                    options?.Reasoning?.Effort,
                    options?.Reasoning?.Output
                );

            private static IReadOnlyList<AgentTurnRecord> BuildProviderTurns(
                IEnumerable<ChatMessage> messages,
                string? conversationId
            )
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
                IDictionary<string, string> toolIdsByCallId
            )
            {
                var turns = new List<AgentTurnRecord>();
                var textBuilder = new StringBuilder();
                foreach (var content in message.Contents)
                {
                    switch (content)
                    {
                        case TextContent textContent
                            when !string.IsNullOrWhiteSpace(textContent.Text):
                            AppendText(textBuilder, textContent.Text);
                            break;

                        case FunctionCallContent functionCall:
                            FlushProviderTextTurn(message, sessionId, turns, textBuilder);
                            if (
                                !string.IsNullOrWhiteSpace(functionCall.CallId)
                                && !string.IsNullOrWhiteSpace(functionCall.Name)
                            )
                            {
                                toolIdsByCallId[functionCall.CallId] = functionCall.Name;
                            }

                            turns.Add(
                                CreateProviderTurn(
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
                                        SerializeArguments(
                                            functionCall.Arguments
                                                ?? new Dictionary<string, object?>(
                                                    StringComparer.Ordinal
                                                )
                                        ),
                                        null,
                                        null,
                                        null,
                                        false,
                                        false,
                                        null,
                                        null
                                    )
                                )
                            );
                            break;

                        case FunctionResultContent functionResult:
                            FlushProviderTextTurn(message, sessionId, turns, textBuilder);
                            var resultText = RenderFunctionResult(functionResult.Result);
                            toolIdsByCallId.TryGetValue(functionResult.CallId, out var toolId);
                            turns.Add(
                                CreateProviderTurn(
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
                                        null
                                    )
                                )
                            );
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
                StringBuilder textBuilder
            )
            {
                if (textBuilder.Length == 0)
                {
                    return;
                }

                turns.Add(
                    CreateProviderTurn(
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
                            null
                        )
                    )
                );
                textBuilder.Clear();
            }

            private static AgentTurnRecord CreateProviderTurn(
                ChatMessage message,
                Guid sessionId,
                AgentMessageRole role,
                AgentTurnKind kind,
                AgentTurnItemRecord item
            )
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
                    createdAt
                );
            }

            private static IReadOnlyList<AgentToolDescriptor>? BuildProviderToolDescriptors(
                IList<AITool>? tools
            ) =>
                tools is { Count: > 0 }
                    ? tools
                        .Select(tool => new AgentToolDescriptor(
                            tool.Name,
                            tool.Name,
                            tool.Description ?? string.Empty,
                            ArgumentsJsonSchema: tool is AIFunctionDeclaration functionDeclaration
                            && functionDeclaration.JsonSchema.ValueKind != JsonValueKind.Undefined
                                ? functionDeclaration.JsonSchema.GetRawText()
                                : null
                        ))
                        .ToArray()
                    : null;

            private static AgentMessageRole ToAgentRole(ChatRole role) =>
                role == ChatRole.System ? AgentMessageRole.System
                : role == ChatRole.Assistant ? AgentMessageRole.Assistant
                : role == ChatRole.Tool ? AgentMessageRole.Tool
                : AgentMessageRole.User;

            private static ChatResponseUpdate CreateToolCallUpdate(
                AgentToolCallRequest toolCall,
                string responseId,
                string messageId,
                string modelId
            ) => CreateToolCallUpdate([toolCall], responseId, messageId, modelId);

            private static ChatResponseUpdate CreateToolCallUpdate(
                IReadOnlyList<AgentToolCallRequest> toolCalls,
                string responseId,
                string messageId,
                string modelId
            ) =>
                new(
                    ChatRole.Assistant,
                    toolCalls
                        .Select(toolCall => new FunctionCallContent(
                            toolCall.CallId,
                            toolCall.ToolId,
                            ParseArguments(toolCall.ArgumentsJson)
                        ))
                        .ToArray()
                )
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

                    return document
                        .RootElement.EnumerateObject()
                        .ToDictionary(
                            property => property.Name,
                            property => (object?)property.Value.Clone(),
                            StringComparer.Ordinal
                        );
                }
                catch (JsonException)
                {
                    return new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["value"] = argumentsJson,
                    };
                }
            }

            private static string SerializeArguments(IDictionary<string, object?> arguments) =>
                arguments.Count == 0 ? "{}" : JsonSerializer.Serialize(arguments);

            private static string RenderFunctionResult(object? result) =>
                result switch
                {
                    null => string.Empty,
                    string text => text,
                    JsonElement jsonElement => jsonElement.GetRawText(),
                    _ => JsonSerializer.Serialize(result),
                };
        }
    }

    private sealed class TestPackageContext(
        string rootPath,
        IReadOnlyDictionary<string, string>? configurationValues = null,
        IReadOnlyDictionary<string, string>? secretValues = null
    ) : IPackageContext
    {
        private readonly TestPackageStorageContext _storage = new(rootPath);
        private readonly InMemoryPackageSettings _settings = new(configurationValues);
        private readonly InMemoryPackageSecrets _secrets = new(secretValues);

        public string PackageId => "test.package.agent";

        public string Version => "1.0.0";

        public string ContentRootPath => rootPath;

        public IPackageStorageContext Storage => _storage;

        public IPackageSettings Settings => _settings;

        public IPackageSecrets Secrets => _secrets;


        public Sunder.Sdk.Logging.IPackageLogging Logging { get; } =
            Sunder.Sdk.Logging.NullPackageLogging.Instance;
    }

    private sealed class TestPackageStorageContext : IPackageStorageContext
    {
        public TestPackageStorageContext(string rootPath)
        {
            Directory.CreateDirectory(rootPath);
            Files = new NullPackageFileStore(rootPath);
            State = new NullPackageKeyValueStore();
            RoleLocalWorkspace = new TestPackageRoleLocalWorkspace(rootPath);
        }

        public IPackageFileStore Files { get; }

        public IPackageKeyValueStore State { get; }
        public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; }
    }

    private sealed class NullPackageFileStore(string rootPath) : TestPackageFileStoreBase(rootPath);

    private sealed class NullPackageKeyValueStore : IPackageKeyValueStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(
            string key,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.GetValueOrDefault(key));
        }

        public Task SetValueAsync(
            string key,
            string value,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> ContainsKeyAsync(
            string key,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.ContainsKey(key));
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(
            string? prefix = null,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Prefix(prefix);
            return Task.FromResult<IReadOnlyList<string>>(
                _values
                    .Keys.Where(key =>
                        prefix is null || key.StartsWith(prefix, StringComparison.Ordinal)
                    )
                    .Order(StringComparer.Ordinal)
                    .ToArray()
            );
        }
    }

    private sealed class InMemoryPackageSettings(IReadOnlyDictionary<string, string>? values)
        : IPackageSettings
    {
        private readonly Dictionary<string, string> _values = CreateValues(values);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        }

        public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
            => GetValueAsync(key, cancellationToken);

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            _values.Remove(key);
            return Task.CompletedTask;
        }

        private static Dictionary<string, string> CreateValues(IReadOnlyDictionary<string, string>? values)
        {
            var result = values is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(values, StringComparer.Ordinal);
            foreach (var pair in result)
            {
                TestPackageStorageGuards.Key(pair.Key);
                TestPackageStorageGuards.Value(pair.Value);
            }
            return result;
        }
    }

    private sealed class InMemoryPackageSecrets(IReadOnlyDictionary<string, string>? values)
        : IPackageSecrets
    {
        private readonly Dictionary<string, string> _values = CreateValues(values);

        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        }

        public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            TestPackageStorageGuards.Value(value);
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestPackageStorageGuards.Key(key);
            _values.Remove(key);
            return Task.CompletedTask;
        }

        private static Dictionary<string, string> CreateValues(IReadOnlyDictionary<string, string>? values)
        {
            var result = values is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(values, StringComparer.Ordinal);
            foreach (var pair in result)
            {
                TestPackageStorageGuards.Key(pair.Key);
                TestPackageStorageGuards.Value(pair.Value);
            }
            return result;
        }
    }
}
