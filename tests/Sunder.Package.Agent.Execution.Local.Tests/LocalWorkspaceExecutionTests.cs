extern alias LocalWorker;

using LocalWorker::Sunder.Agent.Execution.Common;
using LocalWorker::Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Contracts.Models;
using Xunit;
using LocalWorkerModels = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using static Sunder.Package.Agent.Tests.WorkspaceServiceFixture;
using static Sunder.Package.Agent.Tests.WorkspaceStorageFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Workspace component behavior")]
public sealed class LocalWorkspaceExecutionTests
{
    [Fact]
    public async Task LocalExecutionTarget_RejectsPathEscapes()
    {
        using var scope = TestScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        Directory.CreateDirectory(root);
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        var shellCatalogService = new LocalShellCatalogService(scope.Context);
        var target = new LocalExecutionTarget(scope.Context, configService, shellCatalogService);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);

        var result = await target.ReadFileAsync(
            new LocalWorkerModels.CYounics_Ai_Agent_Execution_Target_ExecutionTargetReadFileRequest
            {
                Context = ToLocalExecutionContext(new AgentExecutionTargetContext(null, null, workspace, binding)),
                Request = new() { Path = Path.Combine(scope.RootPath, "outside.txt"), Offset = null, Limit = null },
            });

        Assert.True(result.IsError);
        Assert.Equal(AgentFileReadErrorCodes.OutsideConfiguredScope, result.ErrorCode);
        Assert.Empty(result.Content);
    }

    [Fact]
    public async Task LocalExecutionTarget_RejectsBinaryReads()
    {
        using var scope = TestScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        Directory.CreateDirectory(root);
        var binaryPath = Path.Combine(root, "binary.bin");
        await File.WriteAllBytesAsync(binaryPath, [1, 2, 0, 4]);
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        var shellCatalogService = new LocalShellCatalogService(scope.Context);
        var target = new LocalExecutionTarget(scope.Context, configService, shellCatalogService);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);

        var result = await target.ReadFileAsync(
            new LocalWorkerModels.CYounics_Ai_Agent_Execution_Target_ExecutionTargetReadFileRequest
            {
                Context = ToLocalExecutionContext(new AgentExecutionTargetContext(null, null, workspace, binding)),
                Request = new() { Path = binaryPath, Offset = null, Limit = null },
            });

        Assert.True(result.IsError);
        Assert.Equal(AgentFileReadErrorCodes.BinaryFile, result.ErrorCode);
        Assert.Empty(result.Content);
    }

    [Fact]
    public void LocalProcessEnvironment_BuildEffectivePathEntries_AddsMacOsFallbacksAfterInheritedPath()
    {
        var entries = LocalProcessEnvironment.BuildEffectivePathEntries(
            ["/custom/bin"],
            "/usr/bin:/bin",
            "/Users/fredy",
            isWindows: false,
            isMacOS: true).ToArray();

        Assert.Equal("/custom/bin", entries[0]);
        Assert.True(Array.IndexOf(entries, "/opt/homebrew/bin") > Array.IndexOf(entries, "/bin"));
        Assert.Contains("/Users/fredy/.dotnet", entries);
        Assert.Contains("/Users/fredy/.dotnet/tools", entries);
        Assert.Contains("/usr/local/share/dotnet", entries);
        Assert.Contains("/opt/homebrew/bin", entries);

        var resolution = ExecutableResolver.Resolve(
            "dotnet",
            entries,
            path => string.Equals(path, "/opt/homebrew/bin/dotnet", StringComparison.Ordinal),
            isWindows: false);

        Assert.True(resolution.WasResolved);
        Assert.Equal("/opt/homebrew/bin/dotnet", resolution.FileName);
        Assert.Contains("/opt/homebrew/bin/dotnet", resolution.CheckedLocations);
    }

    [Fact]
    public async Task LocalExecutionTarget_ExecuteProcessAsync_ReportsMissingExecutableInsteadOfWorkingDirectory()
    {
        using var scope = TestScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        Directory.CreateDirectory(root);
        var configService = new LocalExecutionWorkspaceConfigService(scope.Context);
        var shellCatalogService = new LocalShellCatalogService(scope.Context);
        var target = new LocalExecutionTarget(scope.Context, configService, shellCatalogService);
        var (workspace, binding) = await CreateLocalWorkspaceAsync(root, configService);
        const string missingExecutable = "sunder-missing-executable-for-test";

        var result = await target.ExecuteProcessAsync(
            new LocalWorkerModels.CYounics_Ai_Agent_Execution_Target_ExecutionTargetExecuteProcessRequest
            {
                Context = ToLocalExecutionContext(new AgentExecutionTargetContext(null, null, workspace, binding)),
                Request = new()
                {
                    FileName = missingExecutable,
                    Arguments = [],
                    TimeoutSeconds = null,
                    WorkingDirectory = null,
                },
            });

        Assert.Equal(127, result.ExitCode);
        Assert.Contains($"Executable '{missingExecutable}' was not found", result.Output);
        Assert.Contains("Checked PATH locations", result.Output);
        Assert.DoesNotContain("Working directory does not exist", result.Output);
    }

    private static Sunder.Sdk.Rpc.SunderRpcOffsetDateTime LocalTime(DateTimeOffset value)
        => new(value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    private static LocalWorkerModels.AgentExecutionTargetContext ToLocalExecutionContext(
        AgentExecutionTargetContext context)
        => new()
        {
            SessionId = context.SessionId,
            ProfileId = context.ProfileId,
            Workspace = new()
            {
                WorkspaceId = context.Workspace.WorkspaceId,
                DisplayName = context.Workspace.DisplayName,
                Description = context.Workspace.Description,
                CreatedAtUtc = LocalTime(context.Workspace.CreatedAtUtc),
                UpdatedAtUtc = LocalTime(context.Workspace.UpdatedAtUtc),
                Paths = context.Workspace.Paths.Select(path => new LocalWorkerModels.AgentWorkspacePathRecord
                {
                    PathId = path.PathId,
                    WorkspaceId = path.WorkspaceId,
                    HostPath = path.HostPath,
                    IsDefault = path.IsDefault,
                    SortOrder = path.SortOrder,
                    CreatedAtUtc = LocalTime(path.CreatedAtUtc),
                    UpdatedAtUtc = LocalTime(path.UpdatedAtUtc),
                }).ToArray(),
                Documents = context.Workspace.Documents.Select(document => new LocalWorkerModels.AgentWorkspaceDocumentRecord
                {
                    DocumentId = document.DocumentId,
                    WorkspaceId = document.WorkspaceId,
                    FilePath = document.FilePath,
                    SortOrder = document.SortOrder,
                    CreatedAtUtc = LocalTime(document.CreatedAtUtc),
                    UpdatedAtUtc = LocalTime(document.UpdatedAtUtc),
                }).ToArray(),
            },
            Binding = new()
            {
                BindingId = context.Binding.BindingId,
                WorkspaceId = context.Binding.WorkspaceId,
                ExtensionPointId = context.Binding.ExtensionPointId,
                ContributionId = context.Binding.ContributionId,
                Role = context.Binding.Role,
                IsEnabled = context.Binding.IsEnabled,
                SortOrder = context.Binding.SortOrder,
                CreatedAtUtc = LocalTime(context.Binding.CreatedAtUtc),
                UpdatedAtUtc = LocalTime(context.Binding.UpdatedAtUtc),
            },
            AllowOutsideConfiguredScope = context.AllowOutsideConfiguredScope,
            ApprovedResourceReferences = context.ApprovedResourceReferences,
            ApprovedResourceClaims = [],
            ApprovedResourceCapabilities = context.ApprovedResourceCapabilities,
            ResourceOperation = null,
            CapturePostMutationResource = context.CapturePostMutationResource,
            ExpectedConfigurationGeneration = context.ExpectedConfigurationGeneration,
        };
}
