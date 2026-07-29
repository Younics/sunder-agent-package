using System.Security.Cryptography;
using Sunder.Agent.Execution.Common;

namespace Sunder.Package.Agent.Execution.Docker;

internal sealed record DockerVerifiedMount(
    DockerExecutionMount Mount,
    LocalSecureRoot Root);

internal sealed record DockerHostAccessPolicy(
    string ContainerUser,
    string Signature)
{
    public static DockerHostAccessPolicy Resolve()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Strict Docker bind-mount identity and mutation verification is unavailable on Windows.");
        }
        var user = $"{LocalUnixNative.GetEffectiveUserId()}:{LocalUnixNative.GetEffectiveGroupId()}";
        return new DockerHostAccessPolicy(user, "host-posix-owner-v1:" + user);
    }
}

internal interface IDockerMountIdentityVerifier
{
    Task VerifyAsync(
        string container,
        DockerExecutionRuntimeConfig config,
        IReadOnlyList<DockerVerifiedMount> mounts,
        Func<IReadOnlyList<string>, CancellationToken, Task<DockerCliRunResult>> runDockerAsync,
        CancellationToken cancellationToken);
}

internal sealed class DockerMountIdentityVerifier : IDockerMountIdentityVerifier
{
    public async Task VerifyAsync(
        string container,
        DockerExecutionRuntimeConfig config,
        IReadOnlyList<DockerVerifiedMount> mounts,
        Func<IReadOnlyList<string>, CancellationToken, Task<DockerCliRunResult>> runDockerAsync,
        CancellationToken cancellationToken)
    {
        var shell = DockerCommandRunner.ResolveShellPath(config);
        foreach (var verified in mounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var challengeValue = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                var responseValue = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                using var challenge = HostSecureMountChallenge.Create(
                    verified.Root,
                    challengeValue,
                    cancellationToken);
                var challengePath = JoinContainerPath(verified.Mount.ContainerPath, challenge.DirectoryName);
                var hostFile = JoinContainerPath(challengePath, HostSecureMountChallenge.HostChallengeFileName);
                var nestedDirectory = JoinContainerPath(challengePath, HostSecureMountChallenge.ContainerDirectoryName);
                var responseFile = JoinContainerPath(nestedDirectory, HostSecureMountChallenge.ContainerResponseFileName);
                var verifyCommand = string.Join(
                    " && ",
                    "set -eu",
                    $"test \"$(cat -- {DockerCommandRunner.Quote(hostFile)})\" = {DockerCommandRunner.Quote(challengeValue)}",
                    "umask 077",
                    $"mkdir -- {DockerCommandRunner.Quote(nestedDirectory)}",
                    $"printf '%s' {DockerCommandRunner.Quote(responseValue)} > {DockerCommandRunner.Quote(responseFile)}",
                    $"test \"$(cat -- {DockerCommandRunner.Quote(responseFile)})\" = {DockerCommandRunner.Quote(responseValue)}");
                var verify = await runDockerAsync(
                    ["exec", container, shell, "-c", verifyCommand],
                    cancellationToken).ConfigureAwait(false);
                if (verify.ExitCode != 0)
                {
                    throw new InvalidOperationException(AppendOutput(
                        $"Docker bind mount '{verified.Mount.ContainerPath}' failed its bidirectional identity and nested read/write challenge. The container runs as the host UID/GID and must be able to read, write, and traverse this mount.",
                        verify.Output));
                }

                challenge.VerifyContainerResponse(responseValue);
                var cleanupCommand = string.Join(
                    " && ",
                    "set -eu",
                    $"rm -f -- {DockerCommandRunner.Quote(responseFile)} {DockerCommandRunner.Quote(hostFile)}",
                    $"rmdir -- {DockerCommandRunner.Quote(nestedDirectory)}",
                    $"rmdir -- {DockerCommandRunner.Quote(challengePath)}");
                var cleanup = await runDockerAsync(
                    ["exec", container, shell, "-c", cleanupCommand],
                    CancellationToken.None).ConfigureAwait(false);
                if (cleanup.ExitCode != 0)
                {
                    throw new InvalidOperationException(AppendOutput(
                        $"Docker bind mount '{verified.Mount.ContainerPath}' did not securely remove its identity challenge.",
                        cleanup.Output));
                }
                challenge.VerifyContainerCleanup();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                throw new InvalidOperationException(
                    $"Docker bind mount '{verified.Mount.ContainerPath}' failed its retained-root identity challenge.",
                    ex);
            }
        }
    }

    private static string JoinContainerPath(string parent, string child)
        => parent == "/" ? "/" + child : parent.TrimEnd('/') + "/" + child;

    private static string AppendOutput(string message, string output)
    {
        var trimmed = output.Trim();
        return trimmed.Length == 0 ? message : $"{message} {trimmed}";
    }
}
