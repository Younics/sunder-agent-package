using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Agent.Builder;

public sealed record BuilderProjectValidationResult(BuilderProjectRecord? Project, string? Error)
{
    public bool IsValid => Project is not null && string.IsNullOrWhiteSpace(Error);
}

public sealed record BuilderInitializationResult(
    BuilderProjectRecord Project,
    IReadOnlyList<BuilderPrerequisiteStatus> Prerequisites);

public sealed record BuilderProjectOperationResult(BuilderProjectRecord Project, string RuntimeLog);

public sealed class BuilderProjectApplicationService(
    BuilderSetupService setupService,
    BuilderWorkspaceExecutionService executionService,
    IBuilderProjectStore projectStore,
    BuilderPathService pathService)
{
    public Task<IReadOnlyList<AgentWorkspaceRecord>> ListWorkspacesAsync(
        CancellationToken cancellationToken = default)
        => executionService.ListWorkspacesAsync(cancellationToken);

    public Task<IReadOnlyList<BuilderProjectRecord>> LoadProjectsAsync(CancellationToken cancellationToken = default)
        => projectStore.LoadAsync(cancellationToken);

    public async Task<IReadOnlyList<BuilderPrerequisiteStatus>> CheckSetupAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var execution = await executionService.ResolveAsync(workspaceId, cancellationToken);
        return await setupService.CheckAsync(execution, cancellationToken);
    }

    public async Task<string> InstallDotnetSdkAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var execution = await executionService.ResolveAsync(workspaceId, cancellationToken);
        return await setupService.InstallDotnetSdkAsync(execution, cancellationToken);
    }

    public async Task<string> InstallTemplateAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        var execution = await executionService.ResolveAsync(workspaceId, cancellationToken);
        return await setupService.InstallTemplateAsync(execution, cancellationToken);
    }

    public BuilderProjectValidationResult ValidateProject(
        BuilderProjectRecord project,
        IReadOnlyList<AgentWorkspaceRecord> workspaces,
        bool requireExistingFolder,
        bool requireInitializedPaths)
    {
        if (string.IsNullOrWhiteSpace(project.DisplayName))
        {
            return Invalid("Package name is required.");
        }

        if (!PackageId.TryParse(project.PackageId, out _))
        {
            return Invalid($"Package id must be a lowercase dot-separated ASCII id of at most {PackageId.MaximumLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(project.WorkspaceId))
        {
            return Invalid("Workspace is required.");
        }

        var workspace = workspaces.FirstOrDefault(item => string.Equals(item.WorkspaceId, project.WorkspaceId, StringComparison.OrdinalIgnoreCase));
        if (workspace is null)
        {
            return Invalid("Selected workspace was not found.");
        }

        if (workspace.Paths.Count == 0)
        {
            return Invalid("Selected workspace has no workspace paths.");
        }

        if (string.IsNullOrWhiteSpace(project.WorkspacePathId))
        {
            return Invalid("Workspace path is required.");
        }

        var workspacePath = workspace.Paths.FirstOrDefault(path =>
            string.Equals(path.PathId, project.WorkspacePathId, StringComparison.OrdinalIgnoreCase));
        if (workspacePath is null)
        {
            return Invalid("Selected workspace path was not found.");
        }

        if (!requireInitializedPaths)
        {
            return new BuilderProjectValidationResult(project, null);
        }

        if (string.IsNullOrWhiteSpace(project.ExecutionProjectFolder))
        {
            return Invalid("Initialize the package project first.");
        }

        if (string.IsNullOrWhiteSpace(project.ProjectFolder))
        {
            return Invalid("Host project folder is not available. Reinitialize the package project.");
        }

        try
        {
            project = project with
            {
                ProjectFolder = pathService.ResolveContainedHostPath(project.ProjectFolder, workspacePath.HostPath),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Invalid(ex.Message);
        }

        if (requireExistingFolder && !Directory.Exists(project.ProjectFolder))
        {
            return Invalid("Project folder does not exist.");
        }

        return new BuilderProjectValidationResult(project, null);
    }

    public async Task<BuilderInitializationResult> InitializeProjectAsync(
        BuilderProjectRecord project,
        AgentWorkspacePathRecord workspacePath,
        BackgroundProcessContext context,
        Func<IReadOnlyList<BuilderPrerequisiteStatus>, Task>? prerequisitesChanged = null)
    {
        context.ReportIndeterminate("Resolving workspace execution target...");
        var execution = await executionService.ResolveAsync(project.WorkspaceId, context.CancellationToken);
        var prerequisites = await EnsurePrerequisitesInstalledAsync(execution, context, prerequisitesChanged);
        if (!prerequisites.All(status => status.IsInstalled))
        {
            throw new InvalidOperationException(BuildMissingPrerequisitesMessage(prerequisites));
        }

        context.ReportIndeterminate("Creating Sunder package project...");
        var executionProjectFolder = pathService.ResolveExecutionProjectFolder(execution, workspacePath, project.DisplayName);
        var hostMapping = await execution.MapToHostPathAsync(executionProjectFolder, context.CancellationToken);
        if (!hostMapping.IsInsideAllowedRoot)
        {
            throw new InvalidOperationException("Generated project path is outside the selected workspace paths.");
        }

        // Host mapping is advisory only. The target-owned dotnet process below creates the project;
        // Builder must not turn a point-in-time mapping into a host-side structured mutation.
        var hostProjectFolder = hostMapping.HostPath;

        var workingDirectory = pathService.GetExecutionParentFolder(executionProjectFolder) ?? execution.DefaultExecutionRoot;
        var result = await execution.RunProcessAsync(
            "dotnet",
            BuildTemplateArguments(project, executionProjectFolder),
            workingDirectory,
            cancellationToken: context.CancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.CombinedOutput)
                ? "Package initialization failed."
                : result.CombinedOutput);
        }

        var initialized = project with
        {
            ExecutionProjectFolder = executionProjectFolder,
            ProjectFolder = hostProjectFolder,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        return new BuilderInitializationResult(initialized, prerequisites);
    }

    public async Task<BuilderProjectOperationResult> BuildProjectAsync(
        BuilderProjectRecord project,
        BackgroundProcessContext context)
    {
        context.ReportIndeterminate("Running dotnet build...");
        var execution = await executionService.ResolveAsync(project.WorkspaceId, context.CancellationToken);
        var projectFolder = pathService.ResolveExecutionProjectFolder(project);
        var result = await execution.RunProcessAsync(
            "dotnet",
            ["build", projectFolder],
            projectFolder,
            cancellationToken: context.CancellationToken);
        if (result.ExitCode != 0)
        {
            throw new BuilderProjectOperationException(
                "dotnet build failed. See Builder runtime log for details.",
                string.IsNullOrWhiteSpace(result.CombinedOutput) ? "dotnet build failed." : result.CombinedOutput);
        }

        var updated = project with { UpdatedAtUtc = DateTimeOffset.UtcNow };
        return new BuilderProjectOperationResult(updated, string.Empty);
    }

    public async Task<BuilderProjectOperationResult> PublishProjectAsync(
        BuilderProjectRecord project,
        BackgroundProcessContext context)
    {
        context.ReportIndeterminate("Running dotnet publish...");
        var execution = await executionService.ResolveAsync(project.WorkspaceId, context.CancellationToken);
        var projectFolder = pathService.ResolveExecutionProjectFolder(project);
        var result = await execution.RunProcessAsync(
            "dotnet",
            ["publish", projectFolder],
            projectFolder,
            timeoutSeconds: 900,
            cancellationToken: context.CancellationToken);
        if (result.ExitCode != 0)
        {
            throw new BuilderProjectOperationException(
                "dotnet publish failed. See Builder runtime log for details.",
                string.IsNullOrWhiteSpace(result.CombinedOutput) ? "dotnet publish failed." : result.CombinedOutput);
        }

        return new BuilderProjectOperationResult(
            project with { UpdatedAtUtc = DateTimeOffset.UtcNow },
            string.IsNullOrWhiteSpace(result.CombinedOutput) ? "Publish completed." : result.CombinedOutput);
    }

    public async Task<IReadOnlyList<BuilderPrerequisiteStatus>> EnsurePrerequisitesInstalledAsync(
        string workspaceId,
        BackgroundProcessContext context,
        Func<IReadOnlyList<BuilderPrerequisiteStatus>, Task>? prerequisitesChanged = null)
    {
        context.ReportIndeterminate("Resolving workspace execution target...");
        var execution = await executionService.ResolveAsync(workspaceId, context.CancellationToken);
        return await EnsurePrerequisitesInstalledAsync(execution, context, prerequisitesChanged);
    }

    private async Task<IReadOnlyList<BuilderPrerequisiteStatus>> EnsurePrerequisitesInstalledAsync(
        BuilderWorkspaceExecution execution,
        BackgroundProcessContext context,
        Func<IReadOnlyList<BuilderPrerequisiteStatus>, Task>? prerequisitesChanged)
    {
        context.ReportIndeterminate("Checking package builder prerequisites...");
        var statuses = await CheckAndReportSetupAsync(execution, context.CancellationToken, prerequisitesChanged);
        if (statuses.All(status => status.IsInstalled))
        {
            context.ReportProgress(100, "Package builder setup is already ready.");
            return statuses;
        }

        if (IsMissing(statuses, BuilderPrerequisiteKind.DotnetSdk))
        {
            context.ReportIndeterminate("Downloading .NET SDK installer...");
            var message = await setupService.InstallDotnetSdkAsync(execution, context.CancellationToken);
            context.ReportIndeterminate(message);
            statuses = await CheckAndReportSetupAsync(execution, context.CancellationToken, prerequisitesChanged);
        }

        if (IsMissing(statuses, BuilderPrerequisiteKind.DotnetSdk))
        {
            context.ReportProgress(100, "Complete the .NET SDK installer, then recheck setup.");
            return statuses;
        }

        if (IsMissing(statuses, BuilderPrerequisiteKind.SunderTemplate))
        {
            context.ReportIndeterminate("Installing Sunder package template...");
            var message = await setupService.InstallTemplateAsync(execution, context.CancellationToken);
            context.ReportIndeterminate(message);
            statuses = await CheckAndReportSetupAsync(execution, context.CancellationToken, prerequisitesChanged);
        }

        context.ReportProgress(100, statuses.All(status => status.IsInstalled)
            ? "Package builder setup is ready."
            : "Some prerequisites are still missing.");
        return statuses;
    }

    private async Task<IReadOnlyList<BuilderPrerequisiteStatus>> CheckAndReportSetupAsync(
        BuilderWorkspaceExecution execution,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<BuilderPrerequisiteStatus>, Task>? prerequisitesChanged)
    {
        var statuses = await setupService.CheckAsync(execution, cancellationToken);
        if (prerequisitesChanged is not null)
        {
            await prerequisitesChanged(statuses);
        }

        return statuses;
    }

    private string[] BuildTemplateArguments(BuilderProjectRecord project, string outputFolder)
        =>
        [
            "new",
            "sunder-package",
            "--name",
            pathService.ToProjectName(project.DisplayName),
            "--packageId",
            project.PackageId,
            "--packageName",
            project.DisplayName,
            "--output",
            outputFolder,
            "--createInPlace",
        ];

    private static BuilderProjectValidationResult Invalid(string error)
        => new(null, error);

    private static bool IsMissing(IReadOnlyList<BuilderPrerequisiteStatus> statuses, BuilderPrerequisiteKind kind)
        => statuses.Any(status => status.Kind == kind && !status.IsInstalled);

    private static string BuildMissingPrerequisitesMessage(IReadOnlyList<BuilderPrerequisiteStatus> statuses)
    {
        var missing = statuses
            .Where(status => !status.IsInstalled)
            .Select(status => $"{status.Name}: {status.Detail}")
            .ToArray();
        return missing.Length == 0
            ? "Package builder setup is incomplete."
            : "Package builder setup is incomplete." + Environment.NewLine + string.Join(Environment.NewLine, missing);
    }
}

public sealed class BuilderProjectOperationException(string message, string runtimeLog) : Exception(message)
{
    public string RuntimeLog { get; } = runtimeLog;
}
