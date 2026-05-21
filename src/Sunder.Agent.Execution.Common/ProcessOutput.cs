using System.Text.RegularExpressions;

namespace Sunder.Agent.Execution.Common;

public static partial class ProcessOutput
{
    public static string Truncate(string output, int maxLength, out bool wasTruncated)
    {
        wasTruncated = output.Length > maxLength;
        return wasTruncated
            ? output[..maxLength] + Environment.NewLine + "[output truncated]"
            : output;
    }

    public static string StripAnsiEscapeSequences(string output)
        => string.IsNullOrEmpty(output) ? output : AnsiEscapeRegex().Replace(output, string.Empty);

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\a]*(?:\a|\x1B\\)|\x1B[@-_]")]
    private static partial Regex AnsiEscapeRegex();
}
