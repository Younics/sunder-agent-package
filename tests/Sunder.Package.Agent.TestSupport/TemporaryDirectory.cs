namespace Sunder.Package.Agent.TestSupport;

public sealed class TemporaryDirectory : IDisposable
{
    private int _disposed;

    private TemporaryDirectory(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public Exception? CleanupFailure { get; private set; }

    public static TemporaryDirectory Create(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        var safePurpose = string.Concat(purpose.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-'));
        var temporaryRoot = System.IO.Path.GetTempPath()
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsMacOS()
            && (string.Equals(temporaryRoot, "/var", StringComparison.Ordinal)
                || temporaryRoot.StartsWith("/var/", StringComparison.Ordinal)))
        {
            temporaryRoot = "/private" + temporaryRoot;
        }
        var path = System.IO.Path.Combine(
            temporaryRoot,
            safePurpose,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TemporaryDirectory(System.IO.Path.GetFullPath(path));
    }

    public string GetPath(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var path = segments.Aggregate(Path, System.IO.Path.Combine);
        var fullPath = System.IO.Path.GetFullPath(path);
        var prefix = Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)
                     + System.IO.Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("The requested path escapes the temporary directory.", nameof(segments));
        }
        return fullPath;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception exception)
        {
            CleanupFailure = new IOException(
                $"Temporary test directory cleanup failed for '{Path}'. Remaining entries: {DescribeRemainingEntries(Path)}",
                exception);
        }
    }

    public void AssertCleaned()
    {
        if (CleanupFailure is not null)
        {
            throw CleanupFailure;
        }
        if (Directory.Exists(Path))
        {
            throw new IOException(
                $"Temporary test directory still exists: '{Path}'. Remaining entries: {DescribeRemainingEntries(Path)}");
        }
    }

    private static string DescribeRemainingEntries(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? string.Join(", ", Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                    .Take(20)
                    .Select(entry => System.IO.Path.GetRelativePath(path, entry)))
                : "<none>";
        }
        catch (Exception exception)
        {
            return $"<unavailable: {exception.Message}>";
        }
    }
}
