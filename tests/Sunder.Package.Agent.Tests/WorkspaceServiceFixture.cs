extern alias LocalWorker;

using System.Text.Json;
using LocalWorker::Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using TestPackageContext = Sunder.Package.Agent.Tests.WorkspaceStorageFixture.TestPackageContext;

namespace Sunder.Package.Agent.Tests;

internal static class WorkspaceServiceFixture
{
    internal static async Task<(AgentWorkspaceRecord Workspace, AgentWorkspaceBindingRecord Binding)> CreateLocalWorkspaceAsync(string root, LocalExecutionWorkspaceConfigService configService)
    {
        var workspace = CreateWorkspace(root);
        var binding = CreateBinding(workspace.WorkspaceId);
        await configService.SaveConfigAsync(binding.BindingId, new LocalExecutionWorkspaceConfig(null, []));
        return (workspace, binding);
    }

    internal static AgentToolExecutionContext CreateToolExecutionContext(
        TestExtensionCatalog catalog,
        AgentWorkspaceRecord workspace,
        AgentWorkspaceBindingRecord binding)
        => new(null, Workspace: workspace, ExecutionBinding: binding)
        {
            ExecutionTarget = catalog.GetRequiredProviderAsync(catalog.ExecutionTargets).AsTask().GetAwaiter().GetResult().Reference,
        };

    internal static AgentResourceOperationContext CreateResourceOperation(string actionId)
        => new(
            Guid.NewGuid(),
            1,
            "tool-call",
            actionId,
            0,
            "workspace-generation",
            "binding-generation",
            "younics.ai.agent.tools.files",
            "younics.ai.agent.execution.docker",
            Guid.NewGuid().ToString("N"),
            CanIssueOutsideAuthority: true);

    internal static AgentWorkspaceRecord CreateWorkspace(params string[] workspacePaths)
    {
        var now = DateTimeOffset.UtcNow;
        const string workspaceId = "local-test";
        var paths = workspacePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select((path, index) => new AgentWorkspacePathRecord(
                Guid.NewGuid().ToString("N"),
                workspaceId,
                Path.GetFullPath(path),
                index == 0,
                index,
                now,
                now))
            .ToArray();
        return new AgentWorkspaceRecord("local-test", "Local Test", null, now, now, paths);
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
        while (!condition())
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeoutCts.Token);
        }
    }

    internal static string ApplyPatchArgs(string patchText)
        => JsonSerializer.Serialize(new { patchText });

    internal static WorkspaceViewServices CreateWorkspaceViewServices(TestPackageContext context)
    {
        var store = new AgentLocalStore(context);
        var catalog = new TestExtensionCatalog();
        var sessionService = new AgentSessionService(store);
        var workspaceService = new AgentWorkspaceService(store);
        var executionTargetService = new AgentExecutionTargetService(catalog.ExecutionTargets);
        var toolService = new AgentToolService(sessionService, workspaceService, executionTargetService, catalog.ToolSources, catalog.ObservedToolExecutions);
        var profileService = catalog.CreateProfileService(store, toolService);
        return new WorkspaceViewServices(store, catalog, workspaceService, profileService, executionTargetService);
    }

    internal static AgentProfileRecord CreateProfile(string profileId)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentProfileRecord(
            profileId,
            "Profile",
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            now,
            []);
    }

    internal static AgentWorkspaceBindingRecord CreateBinding(string workspaceId, string contributionId = "local")
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentWorkspaceBindingRecord("binding-test", workspaceId, RegressionTestBindings.ExecutionTargets, contributionId, "primary-execution-target", true, 0, now, now);
    }

    internal sealed record WorkspaceViewServices(
        AgentLocalStore Store,
        TestExtensionCatalog Catalog,
        AgentWorkspaceService WorkspaceService,
        AgentProfileService ProfileService,
        AgentExecutionTargetService ExecutionTargetService) : IDisposable
    {
        public void Dispose()
        {
            try { ProfileService.Dispose(); }
            finally
            {
                try { Catalog.Dispose(); }
                finally { Store.Dispose(); }
            }
        }
    }

    internal sealed class FakeBackgroundTaskQueue : IBackgroundTaskQueue
    {
        private readonly List<BackgroundTaskSnapshot> _snapshots = [];

        public event EventHandler<BackgroundTaskChangedEventArgs>? TaskChanged
        {
            add { }
            remove { }
        }

        public List<BackgroundTaskRequest> Requests { get; } = [];

        public BackgroundTaskSnapshot Enqueue(BackgroundTaskRequest request)
        {
            Requests.Add(request);
            var snapshot = new BackgroundTaskSnapshot(
                Guid.NewGuid(),
                request.Title,
                request.GroupKey,
                request.Indicator,
                request.ConcurrencyMode,
                BackgroundTaskState.Queued,
                "Queued",
                null,
                request.CanCancel,
                request.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                null,
                DateTimeOffset.UtcNow,
                null,
                null);
            _snapshots.Add(snapshot);
            return snapshot;
        }

        public IReadOnlyList<BackgroundTaskSnapshot> ListTasks(string? groupKey = null)
            => _snapshots
                .Where(snapshot => string.IsNullOrWhiteSpace(groupKey)
                                   || string.Equals(snapshot.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        public bool Cancel(Guid processId)
            => _snapshots.Any(snapshot => snapshot.TaskId == processId);
    }

    internal sealed class TestExtensionCatalog : RegressionTestExtensionCatalog;

    internal sealed class FakeExecutionTarget(string shellOutput) : IAgentExecutionTarget, IAgentStructuredFileSearchExecutionTarget
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new("local", "local", "Fake", null, SupportsShell: true, SupportsFiles: true);

        public List<string> ResolvedPaths { get; } = [];

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness("local", "local", AgentExecutionTargetReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor("sh", "POSIX sh", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "Run POSIX shell commands."));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
        {
            ResolvedPaths.Add(path);
            return ValueTask.FromResult(new AgentResolvedResource("file", path, path, AgentPermissionBoundaryIds.ConfiguredScope, true));
        }

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentShellCommandResult(0, shellOutput));

        public ValueTask<AgentFileSearchResult> ExecuteFileSearchAsync(
            AgentExecutionTargetContext context,
            AgentFileSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = shellOutput
                .Split(['\0', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(path => request.Kind == AgentFileSearchKind.Grep
                    ? new AgentFileSearchMatch(path, 1, "match")
                    : new AgentFileSearchMatch(path))
                .ToArray();
            return ValueTask.FromResult(new AgentFileSearchResult(matches));
        }

        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    internal sealed class MutableFileExecutionTarget(string content) : IAgentExecutionTarget
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new("local", "local", "Mutable File Target", null, SupportsShell: true, SupportsFiles: true);

        public string Content { get; private set; } = content;

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionTargetReadiness("local", "local", AgentExecutionTargetReadinessStatus.Ready, "Ready."));

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor("sh", "POSIX sh", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "Run POSIX shell commands."));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentResolvedResource("file", path, path, AgentPermissionBoundaryIds.ConfiguredScope, true));

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentFileReadResult(request.Path, Content));

        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
        {
            Content = request.Content;
            return ValueTask.FromResult(new AgentFileMutationResult(request.Path, $"Wrote {request.Content.Length} character(s)."));
        }

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    internal sealed class CountingExecutionTarget(string targetId) : IAgentExecutionTarget
    {
        private int _readinessCallCount;

        public AgentExecutionTargetDescriptor Descriptor { get; } = new("test", targetId, "Counting Target", null, SupportsShell: true, SupportsFiles: true);

        public int ReadinessCallCount => Volatile.Read(ref _readinessCallCount);

        public AgentExecutionTargetContext? LastContext { get; private set; }

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readinessCallCount);
            LastContext = context;
            return ValueTask.FromResult(new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.Ready, "Ready."));
        }

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(AgentExecutionTargetContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentExecutionShellDescriptor("sh", "POSIX sh", "/bin/sh", AgentShellSyntaxKinds.PosixSh, "Run POSIX shell commands."));

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(AgentExecutionTargetContext context, string path, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentResolvedResource("file", path, path, AgentPermissionBoundaryIds.ConfiguredScope, true));

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(AgentExecutionTargetContext context, AgentShellCommandRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileReadResult> ReadFileAsync(AgentExecutionTargetContext context, AgentFileReadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(AgentExecutionTargetContext context, AgentFileWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(AgentExecutionTargetContext context, AgentFileDeleteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
