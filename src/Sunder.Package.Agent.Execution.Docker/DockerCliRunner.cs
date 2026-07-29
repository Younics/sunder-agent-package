using System.Diagnostics;
using Sunder.Agent.Execution.Common;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

public class DockerCliRunner(IPackageContext packageContext)
{
    private const int MaxOutputLength = 51200;
    private const int EndpointResolutionTimeoutSeconds = 30;
    private readonly SemaphoreSlim _endpointGate = new(1, 1);
    private string? _pinnedEndpoint;

    public async Task<DockerCliRunResult> RunAsync(
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        string? standardInput = null,
        IProgress<string>? progress = null)
    {
        var endpoint = await GetPinnedEndpointAsync(cancellationToken).ConfigureAwait(false);
        return await RunCoreAsync(
            ["--host", endpoint, .. args],
            timeoutSeconds,
            cancellationToken,
            standardInput,
            progress).ConfigureAwait(false);
    }

    public async Task<string> GetPinnedEndpointAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _pinnedEndpoint) is { } current)
        {
            return current;
        }

        await _endpointGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pinnedEndpoint is not null)
            {
                return _pinnedEndpoint;
            }
            _pinnedEndpoint = await ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
            return _pinnedEndpoint;
        }
        finally
        {
            _endpointGate.Release();
        }
    }

    protected virtual async Task<string> ResolveEndpointAsync(CancellationToken cancellationToken)
    {
        var configured = DockerLocalEndpointPolicy.GetConfiguredEndpoint();
        if (configured is not null)
        {
            return configured;
        }

        var context = await RunCoreAsync(
            ["context", "inspect", "--format", "{{.Endpoints.docker.Host}}"],
            EndpointResolutionTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);
        if (context.ExitCode != 0)
        {
            throw new DockerExecutionDomainException(
                "docker.endpoint.resolution-failed",
                "Docker context endpoint could not be resolved.",
                isTransient: true);
        }
        return DockerLocalEndpointPolicy.NormalizeEndpointReportedByDocker(context.Output);
    }

    protected virtual async Task<DockerCliRunResult> RunCoreAsync(
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        string? standardInput = null,
        IProgress<string>? progress = null)
    {
        ProcessStartInfo startInfo;
        try
        {
            startInfo = await DockerCli.CreateStartInfoAsync(
                packageContext,
                args,
                redirectStandardInput: standardInput is not null,
                cancellationToken);
        }
        catch (Exception ex)
        {
            return new DockerCliRunResult(127, $"Failed to start Docker CLI: {FormatStartError(ex)}", TimedOut: false, WasTruncated: false);
        }

        var run = await BoundedProcessRunner.RunAsync(
            startInfo,
            new ProcessRunOptions(timeoutSeconds, MaxOutputLength, standardInput, progress),
            cancellationToken).ConfigureAwait(false);
        if (run.StartException is not null)
        {
            return new DockerCliRunResult(127, $"Failed to start Docker CLI: {FormatStartError(run.StartException)}", TimedOut: false, WasTruncated: false);
        }

        var output = ProcessOutput.Format(
            run,
            MaxOutputLength,
            timeoutMessage: $"Docker command timed out after {timeoutSeconds} seconds.");
        return new DockerCliRunResult(run.ExitCode, output.Content, run.TimedOut, output.WasTruncated);
    }

    private static string FormatStartError(Exception exception)
        => exception.Message.Contains("filename or extension is too long", StringComparison.OrdinalIgnoreCase)
            ? "the generated command line was too long. File content should be streamed through stdin instead of passed as a Docker CLI argument."
            : exception.Message;
}

public sealed record DockerCliRunResult(int ExitCode, string Output, bool TimedOut, bool WasTruncated);
