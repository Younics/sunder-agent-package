namespace Sunder.Package.Agent.Builder;

internal static partial class BuilderToolchainPromotion
{
    private static async Task MoveWindowsAsync(string root, string source, string destination,
        IProgress<string>? progress, CancellationToken token)
    {
        // A child file opened without delete sharing makes MoveDirectory report
        // ACCESS_DENIED, not necessarily SHARING_VIOLATION. Never change its ACL
        // or copy into the destination. Six attempts wait at most 1.55 seconds.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                MoveOnce(root, source, destination, token);
                return;
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 5 or 32 or 33)
            {
                if (attempt == 5)
                    throw new IOException($"Could not move toolchain directory '{source}' to '{destination}' "
                        + $"after 6 attempts (Windows error {error.HResult & 0xffff}, HResult 0x{error.HResult:X8}).", error);
                if (attempt == 0)
                    progress?.Report($"Waiting briefly to finalize the toolchain directory move (Windows error {error.HResult & 0xffff}).");
                await Task.Delay(50 << attempt, token).ConfigureAwait(false);
            }
        }
    }
}
