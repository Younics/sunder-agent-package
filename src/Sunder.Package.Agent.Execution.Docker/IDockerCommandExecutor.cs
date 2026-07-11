namespace Sunder.Package.Agent.Execution.Docker;

internal interface IDockerCommandExecutor
{
    Task<DockerCliRunResult> RunAsync(
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        string? standardInput = null);

    int ResolveDefaultTimeoutSeconds();
}
