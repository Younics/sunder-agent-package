using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderProjectDriver : IBuilderJobDriver
{
    private readonly IBuilderProjectDriverToolchains toolchains;
    private readonly IBuilderLocalProcessRunner processes;
    private readonly BuilderProjectDriverOptions options;
    private readonly BuilderProjectDiscovery discovery;
    private readonly Func<BuilderProjectInspection, string, string, string?, CancellationToken, Task<BuilderProjectInspection>>? saveMetadata;
    private readonly string rid;

    public BuilderProjectDriver(IBuilderProjectDriverToolchains toolchains, IBuilderLocalProcessRunner processes,
        BuilderProjectDriverOptions options, BuilderProjectDiscovery? discovery = null,
        Func<BuilderProjectInspection, string, string, string?, CancellationToken, Task<BuilderProjectInspection>>? saveMetadata = null)
    {
        this.toolchains = toolchains;
        this.processes = processes;
        this.options = options with
        {
            CacheRootPath = BuilderJobValidation.Absolute(options.CacheRootPath, "Builder tools cache"),
            ProjectMutationLockRootPath = BuilderJobValidation.Absolute(options.ProjectMutationLockRootPath, "Builder project locks"),
        };
        this.discovery = discovery ?? new();
        this.saveMetadata = saveMetadata;
        if (!Regex.IsMatch(options.CoordinatedPackageVersion, @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new ArgumentException("An exact coordinated Sunder developer package version is required.");
        if (options.CommandTimeout <= TimeSpan.Zero || options.CommandTimeout > TimeSpan.FromMinutes(30)) throw new ArgumentException("Invalid command deadline.");
        foreach (var path in new[] { options.DotnetTemplatePackagePath, options.NuGetConfigPath, options.NodeCreatorPackagePath, options.NodeMetadataToolCliPath }.Where(path => path is not null))
            _ = BuilderJobValidation.Absolute(path!, "Local tooling override");
        foreach (var (name, path) in options.NodePackageOverrides)
        {
            if (name is not ("@sunder/sdk" or "@sunder/package-tool")) throw new ArgumentException("Only coordinated Sunder SDK/tool packages can be overridden.");
            _ = BuilderJobValidation.Absolute(path, "Local Node package override");
        }
        if (options.NodeRegistryUri is { } registry && (!Uri.TryCreate(registry, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0)) throw new ArgumentException("Invalid Node test registry URI.");
        rid = BuilderToolchainPlatform.Current.DotnetRid;
    }

    public async Task ExecuteAsync(BuilderJobRequest request, IBuilderJobReporter reporter, CancellationToken cancellationToken)
    {
        request = BuilderJobValidation.Normalize(request);
        cancellationToken.ThrowIfCancellationRequested();
        var toolchainProgress = new ToolchainStatusProgress(reporter);
        if (request.Operation == "ensure-sdk")
        {
            reporter.Update(new() { Phase = BuilderJobPhase.CheckingSdk });
            var installed = await toolchains.EnsureAsync(request.Kind, request.ProjectFolder, toolchainProgress, cancellationToken).ConfigureAwait(false);
            reporter.Update(new() { Installation = installed, Message = $"{(request.Kind == BuilderProjectKind.Dotnet ? ".NET SDK" : "Node.js")} {installed.Version} is ready." });
            return;
        }
        if (request.Operation == "repair-sdk")
        {
            var repaired = await toolchains.RepairAsync(request.Installation!, toolchainProgress, cancellationToken).ConfigureAwait(false);
            reporter.Update(new() { Installation = repaired, Message = "Toolchain repaired." });
            return;
        }
        if (request.Operation == "remove-sdk")
        {
            await toolchains.RemoveAsync(request.Installation!, toolchainProgress, cancellationToken).ConfigureAwait(false);
            reporter.Update(new() { Message = "Managed toolchain removed." });
            return;
        }
        using var mutation = AcquireProjectMutation(request.ProjectFolder!);
        if (request.Operation == "create")
        {
            await CreateAsync(request, reporter, cancellationToken).ConfigureAwait(false);
            return;
        }
        var project = await InspectAsync(request, cancellationToken).ConfigureAwait(false);
        reporter.Update(new() { Project = project, Message = "Project source inspected." });
        if (request.Operation == "save-metadata")
        {
            reporter.Update(new() { Phase = BuilderJobPhase.EditingMetadata });
            if (!project.CanEditDisplayName) throw new InvalidOperationException(project.ReadOnlyReason ?? "Project metadata is read-only.");
            if (request.PackageId != project.PackageId && !project.CanEditPackageId)
                throw new InvalidOperationException(project.PackageIdReadOnlyReason ?? "The package ID is read-only.");
            IBuilderProjectDriverSelection? selection = null;
            try
            {
                if (project.Kind == BuilderProjectKind.Node && project.RequiresNodeToolingForIdEdit && project.PackageId != request.PackageId)
                {
                    selection = await toolchains.SelectAsync(project.Kind, project.ProjectFolder, null, toolchainProgress, cancellationToken).ConfigureAwait(false);
                    reporter.Update(new() { Installation = selection.Installation });
                }
                var saved = saveMetadata is not null
                    ? await saveMetadata(request.Expected!, request.DisplayName!, request.PackageId!, selection?.Installation.ExecutablePath, cancellationToken).ConfigureAwait(false)
                    : await new BuilderProjectMetadataService(selection?.Installation.ExecutablePath ?? "", options.NodeMetadataToolCliPath,
                        selection?.CreateProcess([], project.ProjectFolder).Environment, reporter)
                        .SaveAsync(request.Expected!, request.DisplayName!, request.PackageId!, cancellationToken).ConfigureAwait(false);
                reporter.Update(new() { Project = saved, Message = "Source metadata saved. Rebuild to regenerate development output." });
            }
            finally { if (selection is not null) await selection.DisposeAsync().ConfigureAwait(false); }
            return;
        }
        await using var lease = await toolchains.SelectAsync(project.Kind, project.ProjectFolder, null, toolchainProgress, cancellationToken).ConfigureAwait(false);
        reporter.Update(new() { Installation = lease.Installation });
        if (project.Kind == BuilderProjectKind.Dotnet && request.Operation == "watch")
        {
            await WatchDotnetAsync(project, lease, reporter, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (request.Operation == "setup")
            await RestoreAsync(project, lease, reporter, cancellationToken).ConfigureAwait(false);
        if (request.Operation == "archive")
            await ArchiveAsync(project, lease, reporter, cancellationToken).ConfigureAwait(false);
        else
        {
            var development = project.Kind == BuilderProjectKind.Node && request.Operation == "watch"
                ? await PrepareNodeWatchAsync(project, lease, reporter, cancellationToken).ConfigureAwait(false)
                : await BuildAsync(project, lease, reporter, requireApp: false, cancellationToken, restore: request.Operation != "setup").ConfigureAwait(false);
            if (request.Operation == "watch") await WatchAsync(project, development, lease, reporter, cancellationToken).ConfigureAwait(false);
            else reporter.Update(new() { Message = request.Operation == "setup" ? "Setup complete; development package is ready." : "Development build completed." });
        }
    }

    private async Task<BuilderProjectInspection> InspectAsync(BuilderJobRequest request, CancellationToken token)
    {
        var project = await discovery.InspectAsync(request.EntryPoint!, token).ConfigureAwait(false);
        if (project.Kind != request.Kind || !BuilderJobValidation.Same(project.ProjectFolder, request.ProjectFolder!)) throw new ArgumentException("The selected project does not match the job request.");
        if (request.Expected is { } expected && expected.SourceRevision != project.SourceRevision)
            throw new BuilderProjectConflictException("Project source changed since the last inspection. Reload it before retrying.");
        BuilderProjectSource.ValidateId(project.PackageId);
        return project;
    }

    private async Task RestoreAsync(BuilderProjectInspection project, IBuilderProjectDriverSelection selection, IBuilderJobReporter reporter, CancellationToken token)
    {
        reporter.Update(new() { Phase = BuilderJobPhase.Restoring, Message = "Restoring project dependencies." });
        if (project.Kind == BuilderProjectKind.Dotnet)
        {
            await RunAsync(selection, RestoreArguments(project), project.ProjectFolder, reporter, token).ConfigureAwait(false);
        }
        else
        {
            var locked = File.Exists(Path.Combine(project.ProjectFolder, "package-lock.json")) || File.Exists(Path.Combine(project.ProjectFolder, "npm-shrinkwrap.json"));
            await RunAsync(selection, [locked ? "ci" : "install", "--no-audit", "--no-fund"], project.ProjectFolder, reporter, token, npm: true).ConfigureAwait(false);
        }
    }

    private async Task<string> BuildAsync(BuilderProjectInspection project, IBuilderProjectDriverSelection selection,
        IBuilderJobReporter reporter, bool requireApp, CancellationToken token, bool restore = true, string? watchStagingRoot = null)
    {
        var output = watchStagingRoot is null ? BuilderProjectDriverOutputs.DevelopmentFolder(project, "Debug") : Path.Combine(watchStagingRoot, "sunder-dev");
        reporter.Update(new() { Phase = BuilderJobPhase.Building, Message = "Building the current-platform development package." });
        if (project.Kind == BuilderProjectKind.Dotnet)
        {
            // MSBuild's -restore performs restore then a fresh build evaluation in one
            // invocation. Use the direct CLI entry point: dotnet build splits out restore
            // when TargetFramework is explicit. Setup/create already restored with the
            // same build properties.
            // MSBuild remains responsible for current inputs, custom targets and package policy.
            await RunAsync(selection, [.. BuildArguments(project, restore),
                .. watchStagingRoot is null ? Array.Empty<string>() : ["-p:SunderPackageDevelopmentStagingRoot=" + EscapeProperty(watchStagingRoot)]],
                project.ProjectFolder, reporter, token, recoverableBuild: watchStagingRoot is not null).ConfigureAwait(false);
        }
        else
        {
            BuilderProjectDriverOutputs.RequireScript(project.ProjectFolder, "sunder:dev:once");
            await RunAsync(selection, ["run", "sunder:dev:once"], project.ProjectFolder, reporter, token, npm: true).ConfigureAwait(false);
        }
        reporter.Update(new() { Phase = BuilderJobPhase.Validating });
        await BuilderProjectDriverOutputs.ValidateDevelopmentAsync(output, project, rid, requireApp, token).ConfigureAwait(false);
        if (watchStagingRoot is null)
            reporter.Update(new() { Phase = BuilderJobPhase.BuildReady, LastSuccessfulBuildAtUtc = DateTimeOffset.UtcNow,
                Project = await discovery.InspectAsync(project.EntryPoint, token).ConfigureAwait(false), DevelopmentFolder = output });
        return output;
    }

    private async Task<string> PrepareNodeWatchAsync(BuilderProjectInspection project, IBuilderProjectDriverSelection selection,
        IBuilderJobReporter reporter, CancellationToken token)
    {
        var development = BuilderProjectDriverOutputs.DevelopmentFolder(project, "Debug");
        try
        {
            await BuilderProjectDriverOutputs.ValidateDevelopmentAsync(development, project, rid, requireApp: false, token).ConfigureAwait(false);
            // npm's watch command still creates its context and performs the initial rebuild.
            // A verified last-good output only avoids the redundant wrapper dev:once command.
            return development;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // Prepare and validate missing/corrupt output before advertising the watch folder.
        }
        return await BuildAsync(project, selection, reporter, requireApp: false, token).ConfigureAwait(false);
    }

    private async Task WatchAsync(BuilderProjectInspection project, string development, IBuilderProjectDriverSelection selection,
        IBuilderJobReporter reporter, CancellationToken token)
    {
        reporter.Update(new() { Phase = BuilderJobPhase.Watching, Message = "Watching source changes. Keep Builder connected; stop watch before other project mutations.", DevelopmentFolder = development });
        if (project.Kind == BuilderProjectKind.Node)
        {
            BuilderProjectDriverOutputs.RequireScript(project.ProjectFolder, "sunder:dev");
            await RunAsync(selection, ["run", "sunder:dev"], project.ProjectFolder, reporter, token, npm: true, watch: true).ConfigureAwait(false);
        }
        else
        {
            await WatchDotnetAsync(project, selection, reporter, token).ConfigureAwait(false);
        }
        throw new InvalidOperationException("The source watcher exited unexpectedly. Inspect its output and restart watch.");
    }

    private async Task ArchiveAsync(BuilderProjectInspection project, IBuilderProjectDriverSelection selection, IBuilderJobReporter reporter, CancellationToken token)
    {
        reporter.Update(new() { Phase = BuilderJobPhase.Packaging, Message = "Checking reviewed public APIs, freezing current contracts, and creating the package archive." });
        var archive = BuilderProjectDriverOutputs.ArchivePath(project);
        if (project.Kind == BuilderProjectKind.Dotnet)
        {
            await RunAsync(selection, ["msbuild", project.EntryPoint, "-restore", "-t:CheckSunderContracts;FreezeSunderContracts;PackSunderPackage",
                "-p:Configuration=Release", .. BuildProperties(project, "Release"), "-p:SunderPackageOutputPath=" + EscapeProperty(archive)],
                project.ProjectFolder, reporter, token).ConfigureAwait(false);
        }
        else
        {
            foreach (var script in new[] { "contracts:check", "contracts:freeze", "package" })
            {
                BuilderProjectDriverOutputs.RequireScript(project.ProjectFolder, script);
                await RunAsync(selection, ["run", script], project.ProjectFolder, reporter, token, npm: true).ConfigureAwait(false);
            }
        }
        await BuilderProjectDriverOutputs.ValidateArchiveAsync(archive, project, rid, token).ConfigureAwait(false);
        reporter.Update(new() { ArchivePath = archive, Project = await discovery.InspectAsync(project.EntryPoint, token).ConfigureAwait(false),
            Message = "Package archive created. It is ready for a separate Registry publication." });
    }

    private string[] BuildProperties(BuilderProjectInspection project, string configuration) =>
        ["-p:TargetFramework=net10.0", "-p:RuntimeIdentifier=" + rid, "-p:UseSharedCompilation=false", "-p:UseRazorBuildServer=false",
         "-p:SunderPackageComposedOutputRoot=" + EscapeProperty(Path.GetDirectoryName(BuilderProjectDriverOutputs.DevelopmentFolder(project, configuration))!),
         .. options.NuGetConfigPath is { } config ? new[] { "-p:RestoreConfigFile=" + EscapeProperty(config) } : []];

    private Task<BuilderDotnetBuildInputs> DiscoverDotnetInputsAsync(BuilderProjectInspection project,
        IBuilderProjectDriverSelection selection, CancellationToken token)
        => BuilderDotnetBuildInputs.DiscoverAsync(project, [options.CacheRootPath, selection.Installation.RootPath], options.NuGetConfigPath, token);

    private string[] BuildArguments(BuilderProjectInspection project, bool restore) =>
        ["msbuild", project.EntryPoint, .. restore ? new[] { "-restore" } : [], "--disable-build-servers", "-p:Configuration=Debug",
            "-p:RestoreDisableParallel=true", .. BuildProperties(project, "Debug")];

    private string[] RestoreArguments(BuilderProjectInspection project) =>
        ["restore", project.EntryPoint, "--disable-parallel", "--disable-build-servers", "-p:Configuration=Debug", .. BuildProperties(project, "Debug"),
            .. options.NuGetConfigPath is { } config ? new[] { "--configfile", config } : []];

    private static string EscapeProperty(string value)
    {
        foreach (var character in new[] { '%', ';', ',', '$', '@', '(', ')', '\'', '*', '?' })
            value = value.Replace(character.ToString(), "%" + ((int)character).ToString("X2", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return value;
    }

    private async Task<BuilderLocalProcessResult> RunAsync(IBuilderProjectDriverSelection selection, IReadOnlyList<string> arguments, string directory,
        IBuilderJobReporter reporter, CancellationToken token, bool npm = false, bool watch = false, string? cliHome = null, bool recoverableBuild = false)
    {
        var request = CreateProcess(selection, arguments, directory, npm, watch, cliHome);
        var result = await processes.RunAsync(request, new ProcessStatusProgress(reporter, watch && npm), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (result.ExitCode != 0)
        {
            if (result.CombinedOutput.Contains("has unverifiable ownership evidence", StringComparison.Ordinal))
                throw new InvalidOperationException("A generated-output lock has unverifiable ownership evidence. Stop other builds/watchers and inspect the exact lock in the command output before moving it aside. Do not delete the generated output.");
            if (result.CombinedOutput.Contains("waiting for generated output lock", StringComparison.Ordinal))
                throw new InvalidOperationException("The command timed out waiting for a generated-output lock. Another build or watcher may still be using these files. Inspect the exact lock in the command output before retrying.");
            var command = (npm ? "npm" : Path.GetFileName(selection.Installation.ExecutablePath)) + " " + arguments[0];
            if (arguments[0] == "new" && arguments.Count > 1) command += " " + arguments[1];
            // The process runner streams stdout/stderr into Output. Keep the error summary
            // concise instead of copying the same stack trace into status and error surfaces.
            var message = $"{command} failed (exit code {result.ExitCode}). See the command output for details.";
            if (recoverableBuild) throw new BuilderCommandFailedException(message);
            throw new InvalidOperationException(message);
        }
        return result;
    }

    private BuilderLocalProcessRequest CreateProcess(IBuilderProjectDriverSelection selection, IReadOnlyList<string> arguments, string directory,
        bool npm = false, bool watch = false, string? cliHome = null)
    {
        var request = selection.CreateProcess(arguments, directory, npm);
        var environment = new Dictionary<string, string?>(request.Environment, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            ["NUGET_PACKAGES"] = Path.Combine(options.CacheRootPath, "nuget-packages"), ["NO_COLOR"] = "1", ["TERM"] = "dumb",
            ["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"] = "1", ["DOTNET_WATCH_SUPPRESS_EMOJIS"] = "1",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0", ["MSBUILDDISABLENODEREUSE"] = "1",
            // The Avalonia collector detaches from MSBuild and would outlive the job/SDK lease.
            ["AVALONIA_TELEMETRY_OPTOUT"] = "1",
            // Opt-in status only. These lines never authorize reload, output reuse or cleanup.
            ["SUNDER_BUILDER_WATCH_PROGRESS"] = watch && npm ? "1" : null,
        };
        if (cliHome is not null) environment["DOTNET_CLI_HOME"] = cliHome;
        if (options.NuGetConfigPath is { } config)
        {
            environment["RestoreConfigFile"] = config;
            environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(options.CacheRootPath, "nuget-http");
            environment["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(options.CacheRootPath, "nuget-plugins");
            environment["NUGET_SCRATCH"] = Path.Combine(options.CacheRootPath, "nuget-scratch");
            environment["NUGET_FALLBACK_PACKAGES"] = null;
        }
        if (options.NodeRegistryUri is not null) environment["NPM_CONFIG_REGISTRY"] = options.NodeRegistryUri;
        return request with { Environment = environment, Timeout = watch ? null : options.CommandTimeout, MaximumCapturedCharacters = 16 * 1024 };
    }

    // SDK progress describes the current phase/download sample; process output is an
    // append-only stream. A synchronous adapter preserves ordering through the final
    // Ready update (Progress<T> could dispatch an older sample after completion).
    private sealed class ToolchainStatusProgress(IBuilderJobReporter reporter) : IProgress<string>
    {
        public void Report(string value) => reporter.Update(new() { Message = value });
    }

    // Only an ordinary nonzero command exit is recoverable in Watch. Ownership,
    // process-start/drain, timeout, watcher and output-validation failures stay terminal.
    private sealed class BuilderCommandFailedException(string message) : InvalidOperationException(message);
}
