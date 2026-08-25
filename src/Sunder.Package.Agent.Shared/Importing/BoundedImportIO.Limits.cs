namespace Sunder.Package.Agent.Shared.Importing;

internal static partial class BoundedImportIO
{
    private static void ValidateLimits(ImportTreeLimits limits)
    {
        if (limits.MaxFiles <= 0
            || limits.MaxDirectories <= 0
            || limits.MaxPathDepth <= 0
            || limits.MaxFileBytes < 0
            || limits.MaxTotalBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }
    }

    private static InvalidDataException Changed(string path)
        => new($"Import source changed while its secure snapshot was being created: '{path}'.");

    private static string Describe(ImportNodeKind kind)
        => kind == ImportNodeKind.Directory ? "directory" : "regular file";

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
