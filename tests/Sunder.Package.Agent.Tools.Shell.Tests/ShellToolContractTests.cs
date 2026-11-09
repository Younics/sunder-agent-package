extern alias ShellWorker;

using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Tests;
using ShellWorker::Sunder.Package.Agent.Tools.Shell;
using ShellModels = ShellWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class ShellToolContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(86_401)]
    [InlineData(int.MaxValue)]
    public async Task ShellTool_RejectsOutOfRangeTimeoutBeforeInvokingTarget(int timeoutSeconds)
    {
        var target = new RecordingExecutionTarget();
        var result = await ExecuteAsync(target,
            $$"""{"command":"echo unsafe","timeoutSeconds":{{timeoutSeconds}}}""");
        Assert.True(result.IsError);
        Assert.Equal("shell-arguments-invalid", result.ErrorCode);
        Assert.Equal(0, target.ExecuteCount);
    }

    [Fact]
    public async Task Shell_PreservesBackendTruncationContract()
    {
        var target = new RecordingExecutionTarget
        {
            Result = new AgentShellCommandResult(124, string.Empty, TimedOut: true, WasTruncated: true),
        };
        var result = await ExecuteAsync(target, "{\"command\":\"test\"}");
        Assert.True(result.IsError);
        Assert.True(result.WasTruncated);
        Assert.Equal("memory:memory", result.BackendId);
        Assert.Equal(1, target.ExecuteCount);
    }

    [Fact]
    public async Task InvalidRequestPresentation_UsesSharedMarkdownShape()
    {
        var detail = (await new ShellToolSource().PresentationAsync(new ShellModels.AgentToolPresentationRequest
        {
            ToolId = "shell", ArgumentsJson = "{", IsError = false,
        }))!.DetailMarkdown!;
        Assert.StartsWith("**Request**", detail, StringComparison.Ordinal);
        Assert.Contains("```json\n{\n```", detail.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    private static async Task<AgentToolResult> ExecuteAsync(RecordingExecutionTarget target, string arguments)
    {
        using var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(RegressionTestBindings.ExecutionTargets, target);
        var provider = await catalog.GetRequiredProviderAsync(catalog.ExecutionTargets);
        using var source = new GeneratedToolSourceProbe(catalog, new ShellToolSource(), "younics.ai.agent.tools.shell");
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
        var binding = new AgentWorkspaceBindingRecord("binding", workspace.WorkspaceId,
            RegressionTestBindings.ExecutionTargets, target.Descriptor.TargetId, "primary-execution-target", true, 0, now, now);
        return await source.ExecuteAsync(new AgentToolExecutionContext(null, Workspace: workspace, ExecutionBinding: binding)
        {
            ExecutionTarget = provider.Reference,
        }, new AgentToolRequest("shell", arguments));
    }

    private sealed class RecordingExecutionTarget : IAgentExecutionTarget
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new("memory", "memory", "Memory", null, SupportsShell: true, SupportsFiles: false);
        public int ExecuteCount { get; private set; }
        public AgentShellCommandResult Result { get; init; } = new(0, "unexpected");

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness("memory", "memory", AgentExecutionTargetReadinessStatus.Ready, "Ready."));
        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor("sh", "sh", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "POSIX sh"));
        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            return ValueTask.FromResult(Result);
        }
        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
