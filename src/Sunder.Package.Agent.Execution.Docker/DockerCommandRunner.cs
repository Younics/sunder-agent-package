using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

internal sealed class DockerCommandRunner(IPackageContext packageContext, DockerCliRunner dockerCliRunner) : IDockerCommandExecutor
{
    private const int DefaultTimeoutSeconds = 300;

    public async ValueTask<AgentShellCommandResult> ExecuteShellAsync(
        DockerExecutionRuntimeConfig config,
        string containerName,
        AgentExecutionTargetContext context,
        AgentShellCommandRequest request,
        CancellationToken cancellationToken)
    {
        var workingDirectory = DockerPathResolver.ResolveWorkingDirectory(config, request.WorkingDirectory, context.AllowOutsideConfiguredScope);
        var result = await RunAsync(
            ["exec", "-w", workingDirectory, containerName, ResolveShellPath(config), "-c", ApplyPathEntries(request.Command, config.PathEntries)],
            request.TimeoutSeconds ?? await ResolveDefaultTimeoutSecondsAsync(cancellationToken),
            cancellationToken);
        return new AgentShellCommandResult(result.ExitCode, result.Output, result.TimedOut, workingDirectory, result.WasTruncated);
    }

    public async ValueTask<AgentShellCommandResult> ExecuteProcessAsync(
        DockerExecutionRuntimeConfig config,
        string containerName,
        AgentExecutionTargetContext context,
        AgentProcessCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            return new AgentShellCommandResult(1, "Command file name cannot be empty.");
        }

        var workingDirectory = DockerPathResolver.ResolveWorkingDirectory(config, request.WorkingDirectory, context.AllowOutsideConfiguredScope);
        var dockerArgs = new List<string> { "exec", "-w", workingDirectory, containerName, ResolveShellPath(config), "-c", ApplyPathEntries(BuildProcessCommand(request), config.PathEntries) };
        var result = await RunAsync(dockerArgs, request.TimeoutSeconds ?? await ResolveDefaultTimeoutSecondsAsync(cancellationToken), cancellationToken);
        return new AgentShellCommandResult(result.ExitCode, result.Output, result.TimedOut, workingDirectory, result.WasTruncated);
    }

    public async Task<DockerCliRunResult> RunAsync(
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        string? standardInput = null)
        => await dockerCliRunner.RunAsync(args, timeoutSeconds, cancellationToken, standardInput).ConfigureAwait(false);

    public async Task<int> ResolveDefaultTimeoutSecondsAsync(CancellationToken cancellationToken = default)
        => int.TryParse(await packageContext.Configuration.GetValueAsync("docker.timeoutSeconds.default", cancellationToken), out var parsed) && parsed > 0
            ? parsed
            : DefaultTimeoutSeconds;

    public static AgentExecutionShellDescriptor GetShellDescriptor(DockerExecutionWorkspaceConfig config)
    {
        var shellPath = ResolveShellPath(config);
        var displayName = ResolveShellDisplayName(shellPath);
        return new AgentExecutionShellDescriptor(
            displayName.ToLowerInvariant().Replace(' ', '-'),
            displayName,
            shellPath,
            AgentShellSyntaxKinds.PosixSh,
            $"Run POSIX commands with {shellPath} inside the selected Docker container. Use Linux/POSIX shell syntax.");
    }

    public static string ResolveShellPath(DockerExecutionWorkspaceConfig config)
        => string.IsNullOrWhiteSpace(config.ShellPath)
            ? DockerExecutionWorkspaceConfigService.DefaultShellPath
            : config.ShellPath;

    public static string ResolveShellPath(DockerExecutionRuntimeConfig config)
        => string.IsNullOrWhiteSpace(config.ShellPath)
            ? DockerExecutionWorkspaceConfigService.DefaultShellPath
            : config.ShellPath;

    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string ResolveShellDisplayName(string shellPath)
    {
        var shellName = shellPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(shellName) ? "POSIX shell" : shellName switch
        {
            "bash" => "Bash",
            "sh" => "POSIX sh",
            "zsh" => "Zsh",
            _ => shellName,
        };
    }

    private static string ApplyPathEntries(string command, IReadOnlyList<string>? pathEntries)
    {
        var entries = pathEntries?
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (entries is null || entries.Length == 0)
        {
            return command;
        }

        var pathPrefix = string.Join(':', entries);
        return $"export PATH={Quote(pathPrefix)}:$PATH; {command}";
    }

    private static string BuildProcessCommand(AgentProcessCommandRequest request)
        => string.Join(' ', new[] { Quote(request.FileName) }.Concat(request.Arguments.Select(Quote)));
}
