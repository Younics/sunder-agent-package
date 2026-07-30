using System.Diagnostics;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Local;

internal sealed class LocalProcessExecutor(IPackageContext packageContext)
{
    public async ValueTask<AgentShellCommandResult> ExecuteProcessAsync(
        LocalExecutionRuntimeConfig config,
        AgentExecutionTargetContext context,
        AgentProcessCommandRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            return new AgentShellCommandResult(1, "Command file name cannot be empty.");
        }

        var workingDirectory = LocalPathResolver.ResolveWorkingDirectory(config, request.WorkingDirectory, context.AllowOutsideConfiguredScope);
        var pathEntries = LocalProcessEnvironment.BuildEffectivePathEntries(config.PathEntries);
        var executableResolution = ExecutableResolver.Resolve(
            request.FileName,
            pathEntries,
            File.Exists,
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("PATHEXT"));
        var startInfo = new ProcessStartInfo
        {
            FileName = executableResolution.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        LocalCommandRunner.ApplyPathEnvironment(startInfo, pathEntries);
        return await LocalCommandRunner.ExecuteAsync(
            startInfo,
            request.TimeoutSeconds
            ?? config.DefaultTimeoutSeconds
            ?? await LocalExecutionConfiguration.ResolveDefaultTimeoutSecondsAsync(packageContext, cancellationToken),
            workingDirectory,
            executableResolution,
            cancellationToken);
    }

}
