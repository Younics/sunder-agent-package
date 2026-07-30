using System.Diagnostics;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Local;

internal sealed class LocalShellExecutor(IPackageContext packageContext, LocalShellCatalogService shellCatalogService)
{
    public async Task<AgentExecutionShellDescriptor> GetShellAsync(
        LocalExecutionWorkspaceConfig config,
        CancellationToken cancellationToken = default)
        => GetShellDescriptor(
            await shellCatalogService.ResolveShellAsync(config.SelectedShellId, cancellationToken));

    public static AgentExecutionShellDescriptor GetShellDescriptor(LocalShellDefinition shell)
        => new(
            shell.ShellId,
            shell.DisplayName,
            shell.ExecutablePath,
            shell.SyntaxKind,
            BuildShellDescription(shell));

    public async ValueTask<AgentShellCommandResult> ExecuteShellAsync(
        LocalExecutionRuntimeConfig config,
        AgentExecutionTargetContext context,
        AgentShellCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            return new AgentShellCommandResult(1, "Command cannot be empty.");
        }

        var shell = config.SelectedShell
                    ?? await shellCatalogService.ResolveShellAsync(config.SelectedShellId, cancellationToken);
        var workingDirectory = LocalPathResolver.ResolveWorkingDirectory(config, request.WorkingDirectory, context.AllowOutsideConfiguredScope);
        var pathEntries = LocalProcessEnvironment.BuildEffectivePathEntries(config.PathEntries);
        var startInfo = BuildShellStartInfo(shell, request.Command, workingDirectory);
        LocalCommandRunner.ApplyPathEnvironment(startInfo, pathEntries);
        return await LocalCommandRunner.ExecuteAsync(
            startInfo,
            request.TimeoutSeconds
            ?? config.DefaultTimeoutSeconds
            ?? await LocalExecutionConfiguration.ResolveDefaultTimeoutSecondsAsync(packageContext, cancellationToken),
            workingDirectory,
            executableResolution: null,
            cancellationToken);
    }

    private static ProcessStartInfo BuildShellStartInfo(LocalShellDefinition shell, string command, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = shell.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        switch (shell.SyntaxKind)
        {
            case AgentShellSyntaxKinds.PowerShell:
                startInfo.ArgumentList.Add("-NoLogo");
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-ExecutionPolicy");
                startInfo.ArgumentList.Add("Bypass");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add(command);
                break;

            case AgentShellSyntaxKinds.Cmd:
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/s");
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(command);
                break;

            case AgentShellSyntaxKinds.PosixSh:
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(command);
                break;

            default:
                startInfo.ArgumentList.Add(command);
                break;
        }

        return startInfo;
    }

    private static string BuildShellDescription(LocalShellDefinition shell)
        => shell.SyntaxKind switch
        {
            AgentShellSyntaxKinds.PowerShell => $"Run PowerShell commands with {shell.DisplayName} on the local machine. Use PowerShell syntax such as Get-ChildItem, $HOME, and Join-Path.",
            AgentShellSyntaxKinds.Cmd => $"Run Windows Command Prompt commands with {shell.DisplayName} on the local machine. Use cmd.exe syntax such as dir and %USERPROFILE%.",
            AgentShellSyntaxKinds.PosixSh => $"Run POSIX shell commands with {shell.DisplayName} on the local machine. Use sh-compatible syntax.",
            _ => $"Run commands with custom shell {shell.DisplayName}. Follow its configured syntax kind.",
        };

}
