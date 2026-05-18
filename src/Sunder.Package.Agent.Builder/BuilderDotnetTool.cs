namespace Sunder.Package.Agent.Builder;

internal static class BuilderDotnetTool
{
    public static string ResolveExecutable()
    {
        var userLocal = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(userLocal) ? userLocal : "dotnet";
    }

    public static async Task<BuilderProcessResult> RunAsync(
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
        => await BuilderProcessRunner.RunAsync(ResolveExecutable(), arguments, workingDirectory, cancellationToken);
}
