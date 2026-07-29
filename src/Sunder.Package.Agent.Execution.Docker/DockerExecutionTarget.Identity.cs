namespace Sunder.Package.Agent.Execution.Docker;

public sealed partial class DockerExecutionTarget
{
    private async Task<string> ResolveLocalDaemonIdentityAsync(CancellationToken cancellationToken)
    {
        var endpointIdentity = await _dockerCliRunner.GetPinnedEndpointAsync(cancellationToken).ConfigureAwait(false);
        lock (_daemonIdentityLock)
        {
            if (_pinnedEndpointIdentity is null)
            {
                _pinnedEndpointIdentity = endpointIdentity;
            }
            else if (!string.Equals(_pinnedEndpointIdentity, endpointIdentity, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The Docker daemon endpoint changed after this execution target pinned its local endpoint.");
            }
        }

        var info = await RunDockerAsync(["info", "--format", "{{.ID}}"], cancellationToken);
        var daemonId = info.Output.Trim();
        if (info.ExitCode != 0
            || string.IsNullOrWhiteSpace(daemonId)
            || daemonId.Length > 1024
            || daemonId.Contains('\r')
            || daemonId.Contains('\n'))
        {
            throw new InvalidOperationException(AppendDockerOutput(
                "Docker daemon identity could not be resolved.",
                info.Output));
        }
        var daemonIdentity = endpointIdentity + ":" + daemonId;
        lock (_daemonIdentityLock)
        {
            _verifiedDaemonIdentity = daemonIdentity;
        }
        return daemonIdentity;
    }

    private async Task<DockerCliRunResult> RunDockerAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
        => await _commandRunner.RunAsync(
            args,
            await _commandRunner.ResolveDefaultTimeoutSecondsAsync(cancellationToken),
            cancellationToken);

    private string GetVerifiedDaemonIdentity()
    {
        lock (_daemonIdentityLock)
        {
            return _verifiedDaemonIdentity
                   ?? throw new InvalidOperationException(
                       "The Docker daemon identity has not been verified for structured file execution.");
        }
    }

    private string GetVerifiedContainerSignature(string container)
        => _verifiedContainerSignatures.TryGetValue(container, out var signature)
            ? signature
            : throw new InvalidOperationException(
                $"Docker container '{container}' has not completed strict mount identity verification.");
}
