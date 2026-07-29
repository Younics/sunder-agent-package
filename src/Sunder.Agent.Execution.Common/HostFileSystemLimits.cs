using System.Text;

namespace Sunder.Agent.Execution.Common;

internal static class HostFileSystemLimits
{
    public const int MaxTraversalDepth = 256;
    public const int MaxTraversalEntries = 100_000;
    public const long MaxTraversalNameBytes = 16L * 1024 * 1024;
    public const long MaxRangedReadBytes = 16L * 1024 * 1024;
    public const long MaxSearchFileBytes = 16L * 1024 * 1024;
    public const long MaxSearchTotalBytes = 64L * 1024 * 1024;
    public const int MaxLineCharacters = 1024 * 1024;
}

internal sealed class HostTraversalBudget(CancellationToken cancellationToken)
{
    private int _entries;
    private long _nameBytes;

    public void Visit(int depth, string name)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > HostFileSystemLimits.MaxTraversalDepth)
        {
            throw new LocalSecurePathException(
                $"Secure traversal exceeds the {HostFileSystemLimits.MaxTraversalDepth}-directory depth limit.");
        }
        if (++_entries > HostFileSystemLimits.MaxTraversalEntries)
        {
            throw new LocalSecurePathException(
                $"Secure traversal exceeds the {HostFileSystemLimits.MaxTraversalEntries}-entry limit.");
        }
        _nameBytes += Encoding.UTF8.GetByteCount(name);
        if (_nameBytes > HostFileSystemLimits.MaxTraversalNameBytes)
        {
            throw new LocalSecurePathException(
                $"Secure traversal exceeds the {HostFileSystemLimits.MaxTraversalNameBytes}-byte name budget.");
        }
    }
}

internal readonly record struct HostLineScanResult(
    int TotalLines,
    bool ContainsNull,
    bool InvalidEncoding,
    bool ExceededByteLimit,
    bool ExceededLineLimit);

internal static class HostBoundedLineReader
{
    public static async Task<HostLineScanResult> ScanAsync(
        FileStream stream,
        long maximumBytes,
        int maximumLineCharacters,
        bool throwOnInvalidBytes,
        Action<int, string> onLine,
        CancellationToken cancellationToken)
    {
        if (stream.Length > maximumBytes)
        {
            return new HostLineScanResult(0, false, false, true, false);
        }

        stream.Position = 0;
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, throwOnInvalidBytes),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        var buffer = new char[4096];
        var line = new StringBuilder(Math.Min(maximumLineCharacters, buffer.Length));
        var lineNumber = 0;
        var containsNull = false;
        var exceededBytes = false;
        var exceededLine = false;
        var currentLineExceeded = false;
        var previousWasCarriageReturn = false;

        void CompleteLine()
        {
            lineNumber++;
            if (!currentLineExceeded)
            {
                onLine(lineNumber, line.ToString());
            }
            line.Clear();
            currentLineExceeded = false;
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                if (stream.Position > maximumBytes)
                {
                    exceededBytes = true;
                    break;
                }

                foreach (var character in buffer.AsSpan(0, read))
                {
                    if (character == '\0')
                    {
                        containsNull = true;
                    }
                    if (character == '\n')
                    {
                        if (previousWasCarriageReturn)
                        {
                            previousWasCarriageReturn = false;
                            continue;
                        }
                        CompleteLine();
                        continue;
                    }
                    if (character == '\r')
                    {
                        CompleteLine();
                        previousWasCarriageReturn = true;
                        continue;
                    }

                    previousWasCarriageReturn = false;
                    if (currentLineExceeded)
                    {
                        continue;
                    }
                    if (line.Length >= maximumLineCharacters)
                    {
                        exceededLine = true;
                        currentLineExceeded = true;
                        line.Clear();
                        continue;
                    }
                    line.Append(character);
                }
            }
        }
        catch (DecoderFallbackException)
        {
            return new HostLineScanResult(lineNumber, containsNull, true, exceededBytes, exceededLine);
        }

        if (!exceededBytes && (line.Length > 0 || currentLineExceeded))
        {
            CompleteLine();
        }
        return new HostLineScanResult(lineNumber, containsNull, false, exceededBytes, exceededLine);
    }
}
