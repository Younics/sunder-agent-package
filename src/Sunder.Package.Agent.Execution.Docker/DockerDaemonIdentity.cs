namespace Sunder.Package.Agent.Execution.Docker;

internal static class DockerDaemonIdentity
{
    public static string Parse(string endpointIdentity, string output)
    {
        var fields = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (output.Length > 1024 || output.Trim().AsSpan().ContainsAny('\r', '\n')
            || fields.Length != 2 || fields[0].Any(char.IsWhiteSpace) || fields[1] != "linux")
            throw new InvalidOperationException("Docker daemon identity could not be resolved. A local Linux-container daemon is required.");
        return endpointIdentity + ":linux:" + fields[0];
    }
}
