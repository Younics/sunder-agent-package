namespace Sunder.Package.Agent.Builder;

internal static partial class BuilderToolchainPromotion
{
    internal static Task MoveAsync(string root, string source, string destination,
        IProgress<string>? progress, CancellationToken token)
    {
        if (OperatingSystem.IsWindows()) return MoveWindowsAsync(root, source, destination, progress, token);
        MoveOnce(root, source, destination, token);
        return Task.CompletedTask;
    }

    private static void MoveOnce(string root, string source, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        BuilderToolchainFiles.CheckPath(root, source);
        BuilderToolchainFiles.CheckPath(root, destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"Toolchain promotion destination already exists: {destination}");
        Directory.Move(source, destination);
    }
}
