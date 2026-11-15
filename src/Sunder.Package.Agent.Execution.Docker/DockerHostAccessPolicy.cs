using Sunder.Agent.Execution.Common;

namespace Sunder.Package.Agent.Execution.Docker;

internal sealed record DockerHostAccessPolicy(string ContainerUser, string Signature)
{
    public static DockerHostAccessPolicy Resolve()
    {
        // Docker Desktop's Linux identity is not a Windows SID/RID. This is an
        // internal, unprivileged Linux principal; each retained bind mount must
        // independently prove its access instead of relying on a UID translation.
        if (OperatingSystem.IsWindows())
            return new DockerHostAccessPolicy("1000:1000", "windows-linux-nonroot-v1:1000:1000");
        var user = $"{LocalUnixNative.GetEffectiveUserId()}:{LocalUnixNative.GetEffectiveGroupId()}";
        return new DockerHostAccessPolicy(user, "host-posix-owner-v1:" + user);
    }
}
