extern alias DockerWorker;

using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;
using DockerWorkerModels = DockerWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using static Sunder.Package.Agent.Tests.WorkspaceServiceFixture;
using static Sunder.Package.Agent.Tests.WorkspaceStorageFixture;

namespace Sunder.Package.Agent.Tests;

internal static class WorkspaceDockerFixture
{
    internal static (AgentWorkspaceRecord Workspace, string HostPath) CreateDockerWorkspace(TestScope scope, string name = "docker-workspace")
    {
        var hostPath = Path.Combine(scope.RootPath, name);
        Directory.CreateDirectory(hostPath);
        return (CreateWorkspace(hostPath), hostPath);
    }

    internal static DockerWorkerModels.AgentWorkspaceRecord ToDockerWorkspace(AgentWorkspaceRecord workspace)
        => new()
        {
            WorkspaceId = workspace.WorkspaceId,
            DisplayName = workspace.DisplayName,
            Description = workspace.Description,
            CreatedAtUtc = DockerTime(workspace.CreatedAtUtc),
            UpdatedAtUtc = DockerTime(workspace.UpdatedAtUtc),
            Paths = workspace.Paths.Select(path => new DockerWorkerModels.AgentWorkspacePathRecord
            {
                PathId = path.PathId,
                WorkspaceId = path.WorkspaceId,
                HostPath = path.HostPath,
                IsDefault = path.IsDefault,
                SortOrder = path.SortOrder,
                CreatedAtUtc = DockerTime(path.CreatedAtUtc),
                UpdatedAtUtc = DockerTime(path.UpdatedAtUtc),
            }).ToArray(),
            Documents = workspace.Documents.Select(document => new DockerWorkerModels.AgentWorkspaceDocumentRecord
            {
                DocumentId = document.DocumentId,
                WorkspaceId = document.WorkspaceId,
                FilePath = document.FilePath,
                SortOrder = document.SortOrder,
                CreatedAtUtc = DockerTime(document.CreatedAtUtc),
                UpdatedAtUtc = DockerTime(document.UpdatedAtUtc),
            }).ToArray(),
        };

    internal static SunderRpcOffsetDateTime DockerTime(DateTimeOffset value)
        => new(value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    internal static FakeDockerCliRunner CreateReadyDockerCliRunner(IPackageContext context)
        => new(context, (args, _, _, _, _) =>
        {
            if (args.Count > 0 && args[0] == "context")
            {
                return Task.FromResult(new DockerCliRunResult(0, "unix:///var/run/docker.sock", false, false));
            }
            if (args.Count > 0 && args[0] == "info")
            {
                return Task.FromResult(new DockerCliRunResult(0, "test-daemon", false, false));
            }
            if (args.Count > 1 && args[0] == "image" && args[1] == "inspect")
            {
                return Task.FromResult(new DockerCliRunResult(
                    0,
                    "sha256:" + new string('a', 64),
                    false,
                    false));
            }
            if (args.Count > 0 && args[0] == "inspect")
            {
                return Task.FromResult(new DockerCliRunResult(1, string.Empty, false, false));
            }
            return Task.FromResult(new DockerCliRunResult(0, string.Empty, false, false));
        });

    internal sealed class PassThroughDockerMountIdentityVerifier : IDockerMountIdentityVerifier
    {
        public int VerifyCount { get; private set; }

        public Task VerifyAsync(
            string container,
            DockerExecutionRuntimeConfig config,
            IReadOnlyList<DockerVerifiedMount> mounts,
            Func<IReadOnlyList<string>, CancellationToken, Task<DockerCliRunResult>> runDockerAsync,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotEmpty(mounts);
            VerifyCount++;
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeDockerCliRunner(
        IPackageContext context,
        Func<IReadOnlyList<string>, int, CancellationToken, string?, IProgress<string>?, Task<DockerCliRunResult>> run,
        Func<string>? endpoint = null)
        : DockerCliRunner(context)
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public List<IReadOnlyList<string>> RawCalls { get; } = [];

        internal override Task<DockerCliResolution> ResolveExecutableAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DockerCliResolution(
                Path.Combine(AppContext.BaseDirectory, "fake-docker"),
                [],
                null));

        protected override Task<string> ResolveEndpointAsync(CancellationToken cancellationToken)
            => Task.FromResult(endpoint?.Invoke() ?? "unix:///var/run/docker.sock");

        protected override async Task<DockerCliRunResult> RunCoreAsync(
            IReadOnlyList<string> args,
            int timeoutSeconds,
            CancellationToken cancellationToken,
            string? standardInput = null,
            IProgress<string>? progress = null)
        {
            RawCalls.Add(args.ToArray());
            var logicalArgs = args.Count >= 2 && args[0] == "--host"
                ? args.Skip(2).ToArray()
                : args.ToArray();
            Calls.Add(logicalArgs);
            var result = await run(logicalArgs, timeoutSeconds, cancellationToken, standardInput, progress);
            return result.ExitCode == 0
                   && string.IsNullOrWhiteSpace(result.Output)
                   && logicalArgs.Length >= 5
                   && logicalArgs[0] == "image"
                   && logicalArgs[1] == "inspect"
                   && logicalArgs[2] == "--format"
                ? result with { Output = "sha256:" + new string('0', 64) }
                : result;
        }
    }
}
