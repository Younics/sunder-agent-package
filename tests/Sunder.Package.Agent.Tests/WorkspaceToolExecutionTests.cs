extern alias FilesWorker;
extern alias ShellWorker;

using FilesWorker::Sunder.Package.Agent.Tools.Files;
using ShellWorker::Sunder.Package.Agent.Tools.Shell;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Xunit;
using static Sunder.Package.Agent.Tests.WorkspaceServiceFixture;
using static Sunder.Package.Agent.Tests.WorkspaceStorageFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Workspace component behavior")]
public sealed class WorkspaceToolExecutionTests
{
    [Fact]
    public async Task AgentToolService_AllowsMcpToolGroupAssignmentBySourceKind()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new TestExtensionCatalog();
        var sessionService = new AgentSessionService(store);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog.ExecutionTargets);
        var toolDescriptor = new AgentToolDescriptor(
            "stitch_create_project",
            "Stitch: Create Project",
            "Create a Stitch project.",
            SourceKind: "mcp",
            SourceId: "stitch-server-id",
            SourceDisplayName: "stitch",
            SelectionScope: AgentToolSelectionScope.Group,
            SelectionGroupId: "stitch-server-id",
            SelectionGroupDisplayName: "stitch");
        catalog.AddProvider(RegressionTestBindings.ToolSources, new StaticToolSource("mcp", "mcp", "Model Context Protocol", [toolDescriptor]));
        var toolService = new AgentToolService(sessionService, workspaceService, executionTargetService, catalog.ToolSources, catalog.ObservedToolExecutions);
        var profile = CreateProfile("profile") with
        {
            SelectableCapabilityAssignments =
            [
                new AgentProfileSelectableCapabilityAssignmentRecord(
                    AgentProfileSelectableCapabilityKinds.ToolGroup,
                    "stitch-server-id",
                    "mcp")
            ]
        };
        var workspace = workspaceService.CreateWorkspace("MCP Workspace");
        var session = sessionService.CreateSession("MCP Session", workspaceId: workspace.WorkspaceId);

        var tools = await toolService.ListReadyRuntimeToolsAsync(profile, session.SessionId, workspace);

        Assert.Contains(tools, tool => string.Equals(tool.Descriptor.ToolId, "stitch_create_project", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FilesToolSource_ReturnsStructuredGlobResults()
    {
        using var scope = TestScope.Create();
        using var catalog = new TestExtensionCatalog();
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, new FakeExecutionTarget("/workspace/one.txt\0/workspace/two.txt\0"));
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        catalog.AddProvider(RegressionTestBindings.ToolSources, source, "younics.ai.agent.tools.files");
        var sourceProvider = await catalog.GetRequiredProviderAsync(catalog.ToolSources);
        var result = await sourceProvider.Service.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("glob", "{\"pattern\":\"*.txt\"}"));

        Assert.False(result.IsError);
        Assert.NotNull(result.StructuredPayloadJson);
        Assert.Contains("one.txt", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("glob", "{\"pattern\":\"*.txt\"}")]
    [InlineData("glob", "{\"pattern\":\"*.txt\",\"path\":null}")]
    [InlineData("glob", "{\"pattern\":\"*.txt\",\"path\":\"\"}")]
    [InlineData("grep", "{\"pattern\":\"needle\"}")]
    [InlineData("grep", "{\"pattern\":\"needle\",\"path\":null}")]
    [InlineData("grep", "{\"pattern\":\"needle\",\"path\":\"\"}")]
    public async Task FilesToolSource_ClassifiesSearchWithoutPathAsDefaultWorkspace(string toolId, string argumentsJson)
    {
        using var scope = TestScope.Create();
        using var catalog = new TestExtensionCatalog();
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, new FakeExecutionTarget(string.Empty));
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        using var sourceProbe = new GeneratedToolSourceProbe(catalog, source, "younics.ai.agent.tools.files");
        var permission = await sourceProbe.BuildPermissionRequestAsync(
            CreateToolExecutionContext(catalog, workspace, binding) with
            {
                ResourceOperation = CreateResourceOperation("files.search"),
            },
            new AgentToolRequest(toolId, argumentsJson));

        Assert.NotNull(permission);
        Assert.Equal("files.search", permission!.ActionId);
        Assert.Equal(AgentPermissionBoundaryIds.ConfiguredScope, permission.BoundaryId);
        Assert.Equal(".", permission.Path);
    }

    [Fact]
    public async Task FilesToolSource_Edit_AcceptsStringReplaceAll()
    {
        using var scope = TestScope.Create();
        using var catalog = new TestExtensionCatalog();
        var target = new MutableFileExecutionTarget("hello hello");
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        catalog.AddProvider(RegressionTestBindings.ToolSources, source, "younics.ai.agent.tools.files");
        var sourceProvider = await catalog.GetRequiredProviderAsync(catalog.ToolSources);
        var result = await sourceProvider.Service.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("edit", "{\"path\":\"notes.txt\",\"oldString\":\"hello\",\"newString\":\"bye\",\"replaceAll\":\"true\"}"));

        Assert.False(result.IsError, result.Content);
        Assert.Equal("bye bye", target.Content);
    }

    [Fact]
    public async Task FilesToolSource_Edit_InvalidReplaceAll_ReturnsToolError()
    {
        using var scope = TestScope.Create();
        using var catalog = new TestExtensionCatalog();
        var target = new MutableFileExecutionTarget("hello");
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        catalog.AddProvider(RegressionTestBindings.ToolSources, source, "younics.ai.agent.tools.files");
        var sourceProvider = await catalog.GetRequiredProviderAsync(catalog.ToolSources);
        var result = await sourceProvider.Service.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("edit", "{\"path\":\"notes.txt\",\"oldString\":\"hello\",\"newString\":\"bye\",\"replaceAll\":\"maybe\"}"));

        Assert.True(result.IsError);
        Assert.Equal("files-arguments-invalid", result.ErrorCode);
        Assert.Contains("replaceAll", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilesToolSource_Read_AcceptsStringOffsetAndLimit()
    {
        using var scope = TestScope.Create();
        using var catalog = new TestExtensionCatalog();
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, new MutableFileExecutionTarget("one\ntwo\nthree"));
        var source = new FilesToolSource(scope.Context);
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        using var sourceProbe = new GeneratedToolSourceProbe(catalog, source, "younics.ai.agent.tools.files");
        var result = await sourceProbe.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("read", "{\"path\":\"notes.txt\",\"offset\":\"2\",\"limit\":\"1\"}"));

        Assert.False(result.IsError, result.Content);
        Assert.Contains("2: two", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("3: three", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShellToolSource_AcceptsStringTimeoutSeconds()
    {
        using var catalog = new TestExtensionCatalog();
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, new ScriptedExecutionTarget("local", "local", [new AgentShellCommandResult(0, "ok")]));
        var source = new ShellToolSource();
        var workspace = CreateWorkspace();
        var binding = CreateBinding(workspace.WorkspaceId);

        catalog.AddProvider(RegressionTestBindings.ToolSources, source, "younics.ai.agent.tools.shell");
        var sourceProvider = await catalog.GetRequiredProviderAsync(catalog.ToolSources);
        var result = await sourceProvider.Service.ExecuteAsync(
            CreateToolExecutionContext(catalog, workspace, binding),
            new AgentToolRequest("shell", "{\"command\":\"echo ok\",\"timeoutSeconds\":\"30\"}"));

        Assert.False(result.IsError, result.Content);
        Assert.Equal("ok", result.Content);
    }

    [Fact]
    public async Task ProfileToolCatalog_DoesNotEvaluateWorkspaceReadiness()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new TestExtensionCatalog();
        catalog.AddProvider(RegressionTestBindings.ToolSources, new FilesToolSource(scope.Context));
        var sessionService = new AgentSessionService(store);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog.ExecutionTargets);
        var toolService = new AgentToolService(sessionService, workspaceService, executionTargetService, catalog.ToolSources, catalog.ObservedToolExecutions);

        var tools = await toolService.ListInstalledLocalToolsAsync();

        var readTool = Assert.Single(tools, tool => tool.Descriptor.ToolId == "read");
        Assert.Equal(AgentToolReadinessStatus.Ready, readTool.Readiness.Status);
        Assert.DoesNotContain("workspace is not bound", readTool.Readiness.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AgentToolDescriptor_DefaultPriority_IsMedium()
    {
        var descriptor = new AgentToolDescriptor("test", "Test", "Test tool.");

        Assert.Equal(AgentToolPriority.Medium, descriptor.Priority);
    }

    [Fact]
    public async Task FilesToolSource_ListToolsAsync_UsesMediumPriorityAndRuntimeGuidance()
    {
        using var scope = TestScope.Create();
        using var catalog = new TestExtensionCatalog();
        var source = new FilesToolSource(scope.Context);
        using var sourceProbe = new GeneratedToolSourceProbe(catalog, source, "younics.ai.agent.tools.files");

        var tools = await sourceProbe.ListToolsAsync(new(null, null));

        Assert.All(tools, tool => Assert.Equal(AgentToolPriority.Medium, tool.Priority));
        Assert.Contains(tools, tool => tool.ToolId == "read" && tool.RuntimeInstructions?.Contains("Use this tool to read file contents", StringComparison.Ordinal) == true);
        Assert.Contains(tools, tool => tool.ToolId == "grep" && tool.RuntimeInstructions?.Contains("Prefer this over shell commands", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ShellToolSource_ListToolsAsync_UsesLowPriorityAndFallbackGuidance()
    {
        using var catalog = new TestExtensionCatalog();
        var source = new ShellToolSource();
        using var sourceProbe = new GeneratedToolSourceProbe(catalog, source, "younics.ai.agent.tools.shell");

        var tools = await sourceProbe.ListToolsAsync(new(null, null));
        var shell = Assert.Single(tools);

        Assert.Equal("shell", shell.ToolId);
        Assert.Equal(AgentToolPriority.Low, shell.Priority);
        Assert.Contains("Use this low-priority tool only", shell.RuntimeInstructions ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentToolService_ListReadyRuntimeToolsAsync_OrdersToolsByPriorityDescending()
    {
        using var scope = TestScope.Create();
        var store = new AgentLocalStore(scope.Context);
        using var catalog = new TestExtensionCatalog();
        catalog.AddTool(new TestTool("low", AgentToolPriority.Low));
        catalog.AddTool(new TestTool("high", AgentToolPriority.High));
        catalog.AddTool(new TestTool("medium", AgentToolPriority.Medium));
        var sessionService = new AgentSessionService(store);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog.ExecutionTargets);
        var toolService = new AgentToolService(sessionService, workspaceService, executionTargetService, catalog.ToolSources, catalog.ObservedToolExecutions);

        var tools = await toolService.ListReadyRuntimeToolsAsync();

        Assert.Equal(new[] { "high", "medium", "low" }, tools.Select(tool => tool.Descriptor.ToolId).ToArray());
    }

    private sealed class TestTool(string toolId, AgentToolPriority priority) : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; } = new(
            toolId,
            toolId,
            "Test tool.",
            Priority: priority);

        public ValueTask<AgentToolReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolReadiness(Descriptor.ToolId, AgentToolReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolResult(request.ToolId, "Executed.", Content: "Executed."));
    }

    private sealed class StaticToolSource(
        string sourceId,
        string sourceKind,
        string displayName,
        IReadOnlyList<AgentToolDescriptor> descriptors) : IAgentToolSource
    {
        public string SourceId { get; } = sourceId;

        public string DisplayName { get; } = displayName;

        public string SourceKind { get; } = sourceKind;

        public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
            AgentToolSourceContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(descriptors);

        public ValueTask<AgentToolReadiness?> GetReadinessAsync(
            string toolId,
            AgentToolSourceContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentToolReadiness?>(descriptors.Any(descriptor => string.Equals(descriptor.ToolId, toolId, StringComparison.OrdinalIgnoreCase))
                ? new AgentToolReadiness(toolId, AgentToolReadinessStatus.Ready, "Ready.")
                : null);

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolResult(request.ToolId, "Executed.", Content: "Executed."));
    }

    private sealed class ScriptedExecutionTarget(string targetKind, string targetId, IEnumerable<AgentShellCommandResult> results) : IAgentExecutionTarget
    {
        private readonly Queue<AgentShellCommandResult> _results = new(results);

        public AgentExecutionTargetDescriptor Descriptor { get; } = new(targetKind, targetId, "Scripted Target", null, SupportsShell: true, SupportsFiles: true);

        public List<string> Commands { get; } = [];

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor("sh", "POSIX sh", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "Run POSIX shell commands."));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentResolvedResource("file", path, path, AgentPermissionBoundaryIds.ConfiguredScope, true));

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
        {
            Commands.Add(request.Command);
            return ValueTask.FromResult(_results.Count == 0
                ? new AgentShellCommandResult(0, string.Empty)
                : _results.Dequeue());
        }

        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
